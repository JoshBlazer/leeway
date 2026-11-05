using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Leeway.Generators;

/// <summary>Turns merge attributes into a schema and a typed API. See docs/design/generator.md.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class LeewayGenerator : IIncrementalGenerator
{
    private const string ClientBase = "Leeway.Datasync.Client.OfflineEntity";

    private static readonly SymbolDisplayFormat NoNullable = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly SymbolDisplayFormat WithNullable = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The output is plain strings, which compare by value, so an unrelated edit does no further work.
        IncrementalValuesProvider<(string Hint, string Source)> sources = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => Transform(ctx, ct))
            .Where(static x => x.Hint.Length > 0)
            .WithTrackingName("LeewaySources");

        context.RegisterSourceOutput(sources.Collect(), static (output, items) =>
        {
            // A partial class has several declarations, and each one produced the same text.
            foreach ((string hint, string source) in items.Distinct())
            {
                output.AddSource(hint, SourceText.From(source, Encoding.UTF8));
            }
        });
    }

    private static (string Hint, string Source) Transform(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node, cancellationToken) is not INamedTypeSymbol type
            || type.IsGenericType
            || type.IsAbstract
            || !IsAccessible(type))
        {
            return (string.Empty, string.Empty);
        }

        List<FieldModel> fields = ReadFields(type);
        if (fields.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        string flatName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace('.', '_');
        string hintName = type.ToDisplayString().Replace('.', '_').Replace('+', '_');
        return ($"{hintName}.Leeway.g.cs", Emit(type, flatName, fields));
    }

    private static bool IsAccessible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<FieldModel> ReadFields(INamedTypeSymbol type)
    {
        List<FieldModel> fields = [];
        HashSet<string> seen = [];
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (IPropertySymbol property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.GetMethod is null
                    || property.SetMethod is null
                    || property.DeclaredAccessibility != Accessibility.Public
                    || property.SetMethod.DeclaredAccessibility != Accessibility.Public
                    || !seen.Add(property.Name))
                {
                    continue;
                }

                foreach (AttributeData attribute in property.GetAttributes())
                {
                    string? kind = attribute.AttributeClass?.ToDisplayString() switch
                    {
                        "Leeway.Core.CounterAttribute" => "Counter",
                        "Leeway.Core.LastWriterWinsAttribute" => "LastWriterWins",
                        "Leeway.Core.AddWinsSetAttribute" => "AddWinsSet",
                        "Leeway.Core.EscrowAttribute" => "Escrow",
                        _ => null,
                    };
                    if (kind is not null)
                    {
                        fields.Add(new FieldModel(property.Name, kind, property.Type.ToDisplayString(WithNullable), Named(attribute, "Min", 0), Named(attribute, "Lease", 1)));
                        break;
                    }
                }
            }
        }

        fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return fields;
    }

    private static long Named(AttributeData attribute, string name, long fallback)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is long value)
            {
                return value;
            }
        }

        return fallback;
    }

    private static string Emit(INamedTypeSymbol type, string flatName, List<FieldModel> fields)
    {
        string entity = type.ToDisplayString(NoNullable);
        string access = EffectiveAccess(type);
        StringBuilder text = new();
        text.AppendLine("// <auto-generated/>");
        text.AppendLine("#nullable enable");
        text.AppendLine();
        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            text.AppendLine($"namespace {type.ContainingNamespace.ToDisplayString()};");
            text.AppendLine();
        }

        text.AppendLine($"/// <summary>The merge schema generated for {flatName}.</summary>");
        text.AppendLine($"internal static class {flatName}LeewaySchema");
        text.AppendLine("{");
        text.AppendLine("    /// <summary>The schema.</summary>");
        text.AppendLine($"    public static readonly global::Leeway.Core.MergeSchema Schema = global::Leeway.Core.MergeSchema.Create(typeof({entity}),");
        text.AppendLine("    [");
        foreach (FieldModel field in fields)
        {
            string limits = field.Kind == "Escrow" ? $", min: {field.Min}, lease: {field.Lease}" : string.Empty;
            text.AppendLine($"        global::Leeway.Core.MergeField.Of<{entity}, {field.Type}>(\"{field.Name}\", global::Leeway.Core.FieldKind.{field.Kind}, static e => e.{field.Name}, static (e, v) => e.{field.Name} = v{limits}),");
        }

        text.AppendLine("    ]);");
        text.AppendLine();
        text.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        text.AppendLine("    internal static void Register() => global::Leeway.Core.MergeSchema.Register(Schema);");
        text.AppendLine("}");

        if (IsClientEntity(type) && type.ContainingType is null)
        {
            EmitTypedApi(text, type, flatName, entity, access, fields);
        }

        return text.ToString();
    }

    private static string EffectiveAccess(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return "internal";
            }
        }

        return "public";
    }

    private static bool IsClientEntity(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == ClientBase)
            {
                return true;
            }
        }

        return false;
    }

    private static void EmitTypedApi(StringBuilder text, INamedTypeSymbol type, string flatName, string entity, string access, List<FieldModel> fields)
    {
        // EditAsync creates a missing entity, so it needs a constructor.
        bool canEdit = type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
        const string Client = "this global::Leeway.Datasync.Client.LeewayClient<TContext> client";
        const string Token = "global::System.Threading.CancellationToken cancellationToken = default";
        const string Constraint = "where TContext : global::Leeway.Datasync.Client.LeewayDbContext";

        bool first = true;
        void Method(string summary, string returns, string name, string parameters, string body)
        {
            if (!first)
            {
                text.AppendLine();
            }

            first = false;
            text.AppendLine($"    /// <summary>{summary}</summary>");
            text.AppendLine($"    public static global::System.Threading.Tasks.Task<{returns}> {name}<TContext>({Client}, string id{parameters}, {Token}) {Constraint}");
            text.AppendLine($"        => {body};");
        }

        text.AppendLine();
        text.AppendLine($"/// <summary>A typed API generated for {flatName}.</summary>");
        text.AppendLine($"{access} static class {flatName}LeewayExtensions");
        text.AppendLine("{");
        foreach (FieldModel field in fields)
        {
            string n = field.Name;
            switch (field.Kind)
            {
                case "Escrow":
                    Method($"Spends from {n}. Within the lease it is confirmed with no signal, and beyond it, provisional.", "global::Leeway.Datasync.Client.SpendResult", $"TrySpend{n}Async", ", long amount", $"client.TrySpendAsync<{entity}>(id, static e => e.{n}, amount, cancellationToken)");
                    Method($"Asks for a lease on {n}, which arrives on the next sync.", "string", $"Request{n}LeaseAsync", ", long amount = 0", $"client.RequestLeaseAsync<{entity}>(id, static e => e.{n}, amount, cancellationToken)");
                    Method($"Gives unused lease on {n} back.", "long", $"Return{n}LeaseAsync", ", long amount", $"client.ReturnLeaseAsync<{entity}>(id, static e => e.{n}, amount, cancellationToken)");
                    Method($"Reads this device's position in {n}.", "global::Leeway.Core.Wallet", $"{n}WalletAsync", string.Empty, $"client.WalletAsync<{entity}>(id, static e => e.{n}, cancellationToken)");
                    break;
                case "Counter" when canEdit:
                    Method($"Adds to {n}. The amount may be negative.", "string", $"Add{n}Async", ", long amount", $"client.EditAsync<{entity}>(id, e => e.Add(static x => x.{n}, amount), cancellationToken)");
                    break;
                case "LastWriterWins" when canEdit:
                    Method($"Sets {n}.", "string", $"Set{n}Async", $", {field.Type} value", $"client.EditAsync<{entity}>(id, e => e.Set(static x => x.{n}, value), cancellationToken)");
                    break;
                case "AddWinsSet" when canEdit:
                    Method($"Adds an element to {n}.", "string", $"AddTo{n}Async", ", string element", $"client.EditAsync<{entity}>(id, e => e.AddToSet(static x => x.{n}, element), cancellationToken)");
                    Method($"Removes an element from {n}. A concurrent add this device has not seen still wins.", "string", $"RemoveFrom{n}Async", ", string element", $"client.EditAsync<{entity}>(id, e => e.RemoveFromSet(static x => x.{n}, element), cancellationToken)");
                    break;
                default:
                    break;
            }
        }

        text.AppendLine("}");
    }

    private sealed class FieldModel(string name, string kind, string type, long min, long lease)
    {
        public string Name { get; } = name;

        public string Kind { get; } = kind;

        public string Type { get; } = type;

        public long Min { get; } = min;

        public long Lease { get; } = lease;
    }
}
