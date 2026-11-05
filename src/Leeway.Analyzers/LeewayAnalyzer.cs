using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Leeway.Analyzers;

/// <summary>The rules for merged entities. See docs/design/generator.md.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LeewayAnalyzer : DiagnosticAnalyzer
{
    /// <summary>LEE001: a decremented numeric field with no merge rule.</summary>
    public static readonly DiagnosticDescriptor NoRuleOnDecrementedField = new(
        "LEE001",
        "Decremented field has no merge rule",
        "'{0}' is decremented but has no merge attribute, so offline changes fall to whole-row sync and nothing stops it crossing a limit",
        "Leeway",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Add [Escrow] if it must not cross a limit, or [Counter] if changes should simply add up.");

    /// <summary>LEE002: a direct write to a merged field.</summary>
    public static readonly DiagnosticDescriptor DirectWrite = new(
        "LEE002",
        "Merged field is written directly",
        "'{0}' is a merged field, so a direct write is lost or refused: change it through LeewayClient",
        "Leeway",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "On a client, saving this is refused. On a server, the next projection overwrites it. Object initializers are allowed, for seeding a new entity.");

    /// <summary>LEE003: a merge attribute on an unsuitable property.</summary>
    public static readonly DiagnosticDescriptor UnsuitableProperty = new(
        "LEE003",
        "Merge attribute on an unsuitable property",
        "[{0}] needs {1}",
        "Leeway",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>LEE004: more than one merge attribute.</summary>
    public static readonly DiagnosticDescriptor SeveralAttributes = new(
        "LEE004",
        "More than one merge attribute",
        "'{0}' has more than one merge attribute, and a field can merge in only one way",
        "Leeway",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>LEE005: a merge attribute outside an entity.</summary>
    public static readonly DiagnosticDescriptor NotAnEntity = new(
        "LEE005",
        "Merge attribute outside an entity",
        "'{0}' is not an OfflineEntity or LeewayEntity, so [{1}] does nothing",
        "Leeway",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>LEE006: a lease that grants nothing.</summary>
    public static readonly DiagnosticDescriptor EmptyLease = new(
        "LEE006",
        "Lease grants nothing",
        "A Lease of {0} grants nothing, so every spend on '{1}' would be provisional",
        "Leeway",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [NoRuleOnDecrementedField, DirectWrite, UnsuitableProperty, SeveralAttributes, NotAnEntity, EmptyLease];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
        context.RegisterOperationAction(AnalyzeIncrement, OperationKind.Increment, OperationKind.Decrement);
        context.RegisterOperationAction(AnalyzeCompound, OperationKind.CompoundAssignment);
        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment);
    }

    private static string? MergeKind(AttributeData attribute) => attribute.AttributeClass?.ToDisplayString() switch
    {
        "Leeway.Core.CounterAttribute" => "Counter",
        "Leeway.Core.LastWriterWinsAttribute" => "LastWriterWins",
        "Leeway.Core.AddWinsSetAttribute" => "AddWinsSet",
        "Leeway.Core.EscrowAttribute" => "Escrow",
        _ => null,
    };

    private static bool HasMergeAttribute(IPropertySymbol property) => property.GetAttributes().Any(a => MergeKind(a) is not null);

    private static bool IsEntity(INamedTypeSymbol? type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() is "Leeway.Datasync.Client.OfflineEntity" or "Leeway.Datasync.Server.LeewayEntity")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNumeric(ITypeSymbol type) => type.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or
        SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

    // ---------- Attributes ----------

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        IPropertySymbol property = (IPropertySymbol)context.Symbol;
        AttributeData[] attributes = property.GetAttributes().Where(a => MergeKind(a) is not null).ToArray();
        if (attributes.Length == 0)
        {
            return;
        }

        AttributeData first = attributes[0];
        if (attributes.Length > 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(SeveralAttributes, NameLocation(first), property.Name));
            return;
        }

        string kind = MergeKind(first)!;
        if (!IsEntity(property.ContainingType))
        {
            context.ReportDiagnostic(Diagnostic.Create(NotAnEntity, NameLocation(first), property.ContainingType.Name, kind));
            return;
        }

        if (Unsuitable(property, kind) is { } requirement)
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsuitableProperty, NameLocation(first), kind, requirement));
            return;
        }

        if (kind == "Escrow")
        {
            foreach (var argument in first.NamedArguments)
            {
                if (argument.Key == "Lease" && argument.Value.Value is long lease && lease < 1)
                {
                    context.ReportDiagnostic(Diagnostic.Create(EmptyLease, first.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation(), lease, property.Name));
                }
            }
        }
    }

    private static string? Unsuitable(IPropertySymbol property, string kind)
    {
        if (property.DeclaredAccessibility != Accessibility.Public
            || property.GetMethod?.DeclaredAccessibility != Accessibility.Public
            || property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
        {
            return "a public getter and setter";
        }

        return kind switch
        {
            "Counter" or "Escrow" when property.Type.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_Int64) => "an int or long property",
            "AddWinsSet" when property.Type.SpecialType != SpecialType.System_String => "a string property, which holds a JSON array",
            _ => null,
        };
    }

    private static Location? NameLocation(AttributeData attribute)
    {
        SyntaxNode? syntax = attribute.ApplicationSyntaxReference?.GetSyntax();
        return syntax is Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax a ? a.Name.GetLocation() : syntax?.GetLocation();
    }

    // ---------- Writes ----------

    private static void AnalyzeIncrement(OperationAnalysisContext context)
    {
        IIncrementOrDecrementOperation operation = (IIncrementOrDecrementOperation)context.Operation;
        Report(context, operation.Target, operation.Kind == OperationKind.Decrement);
    }

    private static void AnalyzeCompound(OperationAnalysisContext context)
    {
        ICompoundAssignmentOperation operation = (ICompoundAssignmentOperation)context.Operation;
        Report(context, operation.Target, operation.OperatorKind == BinaryOperatorKind.Subtract);
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        ISimpleAssignmentOperation operation = (ISimpleAssignmentOperation)context.Operation;
        if (operation.Target is not IPropertyReferenceOperation target)
        {
            return;
        }

        // An object initializer sets up a new entity, which is how a server seeds one.
        if (target.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver })
        {
            return;
        }

        bool decrement = operation.Value is IBinaryOperation { OperatorKind: BinaryOperatorKind.Subtract, LeftOperand: var left }
            && Unwrap(left) is IPropertyReferenceOperation read
            && SymbolEqualityComparer.Default.Equals(read.Property, target.Property);
        Report(context, target, decrement);
    }

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static void Report(OperationAnalysisContext context, IOperation target, bool isDecrement)
    {
        if (target is not IPropertyReferenceOperation { Property: var property } || !IsEntity(property.ContainingType))
        {
            return;
        }

        if (HasMergeAttribute(property))
        {
            context.ReportDiagnostic(Diagnostic.Create(DirectWrite, context.Operation.Syntax.GetLocation(), property.Name));
        }
        else if (isDecrement && IsNumeric(property.Type))
        {
            // The property is passed along so that the code fix can reach it, even in another file.
            ImmutableArray<Location> declared = property.Locations.Where(l => l.IsInSource).ToImmutableArray();
            context.ReportDiagnostic(Diagnostic.Create(NoRuleOnDecrementedField, context.Operation.Syntax.GetLocation(), declared, property.Name));
        }
    }
}
