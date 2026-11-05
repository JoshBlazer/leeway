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
/// <param name="ValueType">The property's type.</param>
/// <param name="Get">Reads the property.</param>
/// <param name="Set">Writes the property.</param>
/// <param name="Min">For an escrowed field, the value it must never go below.</param>
/// <param name="Lease">For an escrowed field, the default lease size.</param>
public sealed record MergeField(string Name, FieldKind Kind, Type ValueType, Func<object, object?> Get, Action<object, object?> Set, long Min = 0, long Lease = 1)
{
    /// <summary>Builds a field from typed accessors. Generated code calls this.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="name">The property name.</param>
    /// <param name="kind">How it merges.</param>
    /// <param name="get">Reads the property.</param>
    /// <param name="set">Writes the property.</param>
    /// <param name="min">For an escrowed field, the value it must never go below.</param>
    /// <param name="lease">For an escrowed field, the default lease size.</param>
    /// <returns>The field.</returns>
    public static MergeField Of<T, TValue>(string name, FieldKind kind, Func<T, TValue> get, Action<T, TValue> set, long min = 0, long lease = 1)
        => new(name, kind, typeof(TValue), e => get((T)e), (e, v) => set((T)e, (TValue)v!), min, lease);
}

/// <summary>
/// The merge semantics of an entity type. It works on both the client and server classes for an entity, as long as
/// they carry the same attributes. Normally generated at compile time, with reflection as the fallback.
/// </summary>
public sealed class MergeSchema
{
    private const string EscrowRegisterPrefix = "$escrow:";
    private static readonly ConcurrentDictionary<Type, MergeSchema> Registered = new();
    private static readonly ConcurrentDictionary<Type, MergeSchema> Reflected = new();

    private MergeSchema(Type entityType, IReadOnlyDictionary<string, MergeField> fields, bool isGenerated)
    {
        EntityType = entityType;
        Fields = fields;
        IsGenerated = isGenerated;
    }

    /// <summary>The entity type.</summary>
    public Type EntityType { get; }

    /// <summary>The fields with merge semantics, by name.</summary>
    public IReadOnlyDictionary<string, MergeField> Fields { get; }

    /// <summary>Whether this came from the source generator, rather than reflection.</summary>
    public bool IsGenerated { get; }

    /// <summary>The escrowed fields.</summary>
    public IEnumerable<MergeField> EscrowFields => Fields.Values.Where(f => f.Kind == FieldKind.Escrow);

    /// <summary>Builds a schema from its fields. Generated code calls this.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="fields">The fields.</param>
    /// <returns>The schema.</returns>
    public static MergeSchema Create(Type entityType, IEnumerable<MergeField> fields) => new(entityType, fields.ToDictionary(f => f.Name), isGenerated: true);

    /// <summary>Registers a generated schema. Generated code calls this.</summary>
    /// <param name="schema">The schema.</param>
    public static void Register(MergeSchema schema) => Registered[schema.EntityType] = schema;

    /// <summary>Finds a type's schema: the generated one if there is one, or else by reflection.</summary>
    /// <param name="type">The entity type.</param>
    /// <returns>The schema.</returns>
    public static MergeSchema For(Type type) => Registered.TryGetValue(type, out MergeSchema? schema) ? schema : FromReflection(type);

    /// <summary>Reads a type's schema from its attributes by reflection.</summary>
    /// <param name="type">The entity type.</param>
    /// <returns>The schema.</returns>
    public static MergeSchema FromReflection(Type type) => Reflected.GetOrAdd(type, static t =>
    {
        Dictionary<string, MergeField> fields = [];
        foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<CounterAttribute>() is not null)
            {
                fields[property.Name] = Reflect(property, FieldKind.Counter);
            }
            else if (property.GetCustomAttribute<LastWriterWinsAttribute>() is not null)
            {
                fields[property.Name] = Reflect(property, FieldKind.LastWriterWins);
            }
            else if (property.GetCustomAttribute<AddWinsSetAttribute>() is not null)
            {
                if (property.PropertyType != typeof(string))
                {
                    throw new InvalidOperationException($"{t.Name}.{property.Name}: [AddWinsSet] needs a string property, which holds a JSON array.");
                }

                fields[property.Name] = Reflect(property, FieldKind.AddWinsSet);
            }
            else if (property.GetCustomAttribute<EscrowAttribute>() is { } escrow)
            {
                fields[property.Name] = Reflect(property, FieldKind.Escrow, escrow.Min, escrow.Lease);
            }
        }

        return new MergeSchema(t, fields, isGenerated: false);
    });

    private static MergeField Reflect(PropertyInfo property, FieldKind kind, long min = 0, long lease = 1)
        => new(property.Name, kind, property.PropertyType, property.GetValue, property.SetValue, min, lease);

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
            object? value = field.Get(entity);
            switch (field.Kind)
            {
                case FieldKind.Counter:
                    document = document.WithCounter(field.Name, PnCounter.Empty.AddDelta("$seed", Convert.ToInt64(value)));
                    break;
                case FieldKind.LastWriterWins when value is not null:
                    document = document.WithRegister(field.Name, new LwwRegister(EncodeValue(value, field.ValueType), stamp));
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
                    field.Set(entity, Convert.ChangeType(document.Counter(field.Name).Value, field.ValueType, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case FieldKind.LastWriterWins when document.Registers.TryGetValue(field.Name, out LwwRegister? register):
                    field.Set(entity, register.Value is null ? null : JsonSerializer.Deserialize(register.Value, field.ValueType));
                    break;
                case FieldKind.AddWinsSet:
                    field.Set(entity, JsonSerializer.Serialize(document.Set(field.Name).Elements.Order(StringComparer.Ordinal).ToArray()));
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
        => field.Set(entity, Convert.ChangeType(field.Min + remaining, field.ValueType, System.Globalization.CultureInfo.InvariantCulture));
}
