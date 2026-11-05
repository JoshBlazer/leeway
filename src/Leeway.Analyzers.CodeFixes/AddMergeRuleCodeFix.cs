using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Leeway.Analyzers.CodeFixes;

/// <summary>For LEE001: adds [Escrow] or [Counter] to the property, wherever it is declared.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddMergeRuleCodeFix))]
[Shared]
public sealed class AddMergeRuleCodeFix : CodeFixProvider
{
    /// <summary>Identifies the [Escrow] fix.</summary>
    public const string EscrowKey = "LeewayAddEscrow";

    /// <summary>Identifies the [Counter] fix.</summary>
    public const string CounterKey = "LeewayAddCounter";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ["LEE001"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Diagnostic diagnostic = context.Diagnostics[0];
        if (diagnostic.AdditionalLocations.Count == 0)
        {
            return Task.CompletedTask;
        }

        Location declared = diagnostic.AdditionalLocations[0];
        context.RegisterCodeFix(
            CodeAction.Create("Add [Escrow], so that it cannot cross a limit", ct => AddAsync(context.Document.Project.Solution, declared, "Escrow(Min = 0, Lease = 1)", ct), EscrowKey),
            diagnostic);
        context.RegisterCodeFix(
            CodeAction.Create("Add [Counter], so that changes add up", ct => AddAsync(context.Document.Project.Solution, declared, "Counter", ct), CounterKey),
            diagnostic);
        return Task.CompletedTask;
    }

    /// <summary>A fix must keep the file's own line endings.</summary>
    private static SyntaxTrivia EndOfLineOf(SyntaxNode root)
    {
        foreach (SyntaxTrivia trivia in root.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                return trivia;
            }
        }

        return SyntaxFactory.EndOfLine("\n");
    }

    private static async Task<Solution> AddAsync(Solution solution, Location declared, string attributeText, CancellationToken cancellationToken)
    {
        Document? document = solution.GetDocument(declared.SourceTree);
        if (document is null || await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not CompilationUnitSyntax root)
        {
            return solution;
        }

        PropertyDeclarationSyntax? property = root.FindNode(declared.SourceSpan).FirstAncestorOrSelf<PropertyDeclarationSyntax>();
        if (property is null)
        {
            return solution;
        }

        // Comments and existing attributes stay above the new attribute.
        SyntaxTrivia endOfLine = EndOfLineOf(root);
        AttributeListSyntax list = SyntaxFactory.ParseCompilationUnit("[" + attributeText + "]\nclass C { }").Members[0].AttributeLists[0].WithTrailingTrivia(endOfLine);
        SyntaxTriviaList leading = property.GetLeadingTrivia();
        SyntaxTriviaList indent = SyntaxFactory.TriviaList(leading.Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse().Take(1));

        // With no attributes yet, the comments move up onto the new list. Otherwise they stay on the first one.
        PropertyDeclarationSyntax updated = property.AttributeLists.Count > 0
            ? property.AddAttributeLists(list.WithLeadingTrivia(indent))
            : property.WithLeadingTrivia(indent).WithAttributeLists(SyntaxFactory.SingletonList(list.WithLeadingTrivia(leading)));

        CompilationUnitSyntax newRoot = root.ReplaceNode(property, updated);
        if (!newRoot.Usings.Any(u => u.Name?.ToString() == "Leeway.Core"))
        {
            UsingDirectiveSyntax directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Leeway.Core").WithLeadingTrivia(SyntaxFactory.Space)).WithTrailingTrivia(endOfLine);
            newRoot = newRoot.WithUsings(newRoot.Usings.Insert(0, directive));
        }

        return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
    }
}
