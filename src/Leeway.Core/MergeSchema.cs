using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Leeway.Core;

/// <summary>Marks a numeric property as a counter that merges by adding everyone's changes.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CounterAttribute : Attribute;

/// <summary>Marks a property as last-writer-wins by hybrid logical clock.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LastWriterWinsAttribute : Attribute;

/// <summary>Marks a string property as an add-wins set of strings, stored as a JSON array.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AddWinsSetAttribute : Attribute;

/// <summary>Marks a numeric property as a bounded quantity that devices spend from leases.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EscrowAttribute : Attribute
{
    /// <summary>The value the property must never go below.</summary>
    public long Min { get; set; }

    /// <summary>The default lease size.</summary>
    public long Lease { get; set; } = 1;
}

/// <summary>How a field merges.</summary>
public enum FieldKind
{
    /// <summary>A counter.</summary>
    Counter,

    /// <summary>A last-writer-wins register.</summary>
    LastWriterWins,

    /// <summary>An add-wins set.</summary>
    AddWinsSet,

    /// <summary>An escrowed quantity.</summary>
    Escrow,
}

/// <summary>A property with merge semantics.</summary>
/// <param name="Name">The property name, used as the field name.</param>
/// <param name="Kind">How it merges.</param>
/// <param name="Property">The property.</param>
/// <param name="Escrow">For an escrowed field, its settings.</param>
public sealed record MergeField(string Name, FieldKind Kind, PropertyInfo Property, EscrowAttribute? Escrow = null);

/// <summary>
/// The merge semantics of an entity type, read from its attributes. It works on both the client and server
/// classes for an entity, as long as they carry the same attributes.
/// </summary>
public sealed class MergeSchema
{
    private const string EscrowRegisterPrefix = "$escrow:";
    private static readonly ConcurrentDictionary<Type, MergeSchema> Cache = new();

    private MergeSchema(IReadOnlyDictionary<string, MergeField> fields) => Fields = fields;

    /// <summary>The fields with merge semantics, by name.</summary>
    public IReadOnlyDictionary<string, MergeField> Fields { get; }

    /// <summary>The escrowed fields.</summary>
    public IEnumerable<MergeField> EscrowFields => Fields.Values.Where(f => f.Kind == FieldKind.Escrow);

    /// <summary>Reads a type's schema.</summary>
    /// <param name="type">The entity type.</param>
    /// <returns>The schema.</returns>
    public static MergeSchema For(Type type) => Cache.GetOrAdd(type, static t =>
    {
        Dictionary<string, MergeField> fields = [];
        foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<CounterAttribute>() is not null)
            {
                fields[property.Name] = new MergeField(property.Name, FieldKind.Counter, property);
            }
            else if (property.GetCustomAttribute<LastWriterWinsAttribute>() is not null)
            {
                fields[property.Name] = new MergeField(property.Name, FieldKind.LastWriterWins, property);
            }
            else if (property.GetCustomAttribute<AddWinsSetAttribute>() is not null)
            {
                if (property.PropertyType != typeof(string))
                {
                    throw new InvalidOperationException($"{t.Name}.{property.Name}: [AddWinsSet] needs a string property, which holds a JSON array.");
                }

                fields[property.Name] = new MergeField(property.Name, FieldKind.AddWinsSet, property);
            }
            else if (property.GetCustomAttribute<EscrowAttribute>() is { } escrow)
            {
                fields[property.Name] = new MergeField(property.Name, FieldKind.Escrow, property, escrow);
            }
        }

        return new MergeSchema(fields);
    });

    /// <summary>Names the resource for an escrowed field.</summary>
    /// <param name="entityType">The entity type name.</param>
    /// <param name="entityId">The entity ID.</param>
    /// <param name="field">The field name.</param>
    /// <returns>The resource name.</returns>
    public static string EscrowResource(string entityType, string entityId, string field) => $"{entityType}/{entityId}/{field}";

    /// <summary>Reads the property name from a lambda such as <c>z => z.Notes</c>.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="selector">The lambda.</param>
    /// <returns>The property name.</returns>
    public static string FieldName<T, TValue>(Expression<Func<T, TValue>> selector)
    {
        Expression body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : selector.Body;
        return body is MemberExpression { Member: PropertyInfo property }
            ? property.Name
            : throw new ArgumentException("Expected a property, such as z => z.Notes.", nameof(selector));
    }

    /// <summary>Encodes a value for a register.</summary>
    /// <param name="value">The value.</param>
    /// <param name="type">Its declared type.</param>
    /// <returns>JSON text.</returns>
    public static string EncodeValue(object? value, Type type) => JsonSerializer.Serialize(value, type);

    /// <summary>Builds a document from an entity's current property values, stamped by the server.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="stamp">The stamp for register values.</param>
    /// <returns>The document.</returns>
    public MergeDocument Seed(object entity, HybridTimestamp stamp)
    {
        MergeDocument document = MergeDocument.Empty;
        foreach (MergeField field in Fields.Values)
        {
            object? value = field.Property.GetValue(entity);
            switch (field.Kind)
            {
                case FieldKind.Counter:
                    document = document.WithCounter(field.Name, PnCounter.Empty.AddDelta("$seed", Convert.ToInt64(value)));
                    break;
                case FieldKind.LastWriterWins when value is not null:
                    document = document.WithRegister(field.Name, new LwwRegister(EncodeValue(value, field.Property.PropertyType), stamp));
                    break;
                case FieldKind.AddWinsSet when value is string json:
                    OrSet set = OrSet.Empty;
                    foreach (string element in JsonSerializer.Deserialize<string[]>(json) ?? [])
                    {
                        set = set.Merge(set.AddDelta(element, "$seed:" + element));
                    }

                    document = document.WithSet(field.Name, set);
                    break;
                default:
                    break;
            }
        }

        return document;
    }

    /// <summary>Writes a document's values to the entity's properties. Escrowed fields are handled separately.</summary>
    /// <param name="document">The document.</param>
    /// <param name="entity">The entity.</param>
    public void Project(MergeDocument document, object entity)
    {
        foreach (MergeField field in Fields.Values)
        {
            switch (field.Kind)
            {
                case FieldKind.Counter:
                    field.Property.SetValue(entity, Convert.ChangeType(document.Counter(field.Name).Value, field.Property.PropertyType));
                    break;
                case FieldKind.LastWriterWins when document.Registers.TryGetValue(field.Name, out LwwRegister? register):
                    field.Property.SetValue(entity, register.Value is null ? null : JsonSerializer.Deserialize(register.Value, field.Property.PropertyType));
                    break;
                case FieldKind.AddWinsSet:
                    field.Property.SetValue(entity, JsonSerializer.Serialize(document.Set(field.Name).Elements.Order(StringComparer.Ordinal).ToArray()));
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Records the server's remaining quantity in a document, so that a local view can always be recomputed from it.</summary>
    /// <param name="document">The document.</param>
    /// <param name="field">The escrowed field.</param>
    /// <param name="remaining">The quantity remaining.</param>
    /// <param name="stamp">The stamp.</param>
    /// <returns>The new document.</returns>
    public static MergeDocument WithEscrowRemaining(MergeDocument document, string field, long remaining, HybridTimestamp stamp)
        => document.WithRegister(EscrowRegisterPrefix + field, new LwwRegister(remaining.ToString(System.Globalization.CultureInfo.InvariantCulture), stamp));

    /// <summary>Reads the server's remaining quantity from a document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="field">The escrowed field.</param>
    /// <returns>The quantity remaining, or zero if unknown.</returns>
    public static long EscrowRemaining(MergeDocument document, string field)
        => document.Registers.TryGetValue(EscrowRegisterPrefix + field, out LwwRegister? register) && register.Value is not null
            ? long.Parse(register.Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

    /// <summary>Writes an escrowed value to its property.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="field">The escrowed field.</param>
    /// <param name="remaining">The quantity remaining above the minimum.</param>
    public static void ProjectEscrow(object entity, MergeField field, long remaining)
        => field.Property.SetValue(entity, Convert.ChangeType(field.Escrow!.Min + remaining, field.Property.PropertyType));
}
