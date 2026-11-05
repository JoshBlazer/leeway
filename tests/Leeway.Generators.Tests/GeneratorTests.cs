using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Leeway.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Leeway.Generators.Tests;

public static class ModuleInit
{
    [ModuleInitializer]
    public static void Init() => VerifySourceGenerators.Initialize();
}

/// <summary>M5 exit check: attributes produce merge code, by snapshot, and that code really compiles.</summary>
public class GeneratorTests
{
    private const string ClientEntity = """
        using Leeway.Core;
        using Leeway.Datasync.Client;

        namespace Venue;

        public class Zone : OfflineEntity
        {
            [Escrow(Min = 5, Lease = 10)] public int RemainingCapacity { get; set; }
            [Counter] public long CheckIns { get; set; }
            [LastWriterWins] public string? Notes { get; set; }
            [LastWriterWins] public int? Priority { get; set; }
            [AddWinsSet] public string? Flags { get; set; }
            public string? NotMerged { get; set; }
        }
        """;

    private const string ServerEntity = """
        using Leeway.Core;
        using Leeway.Datasync.Server;

        namespace Venue.Server;

        public class ServerZone : LeewayEntity
        {
            [Escrow(Lease = 3)] public int RemainingCapacity { get; set; }
            [Counter] public int CheckIns { get; set; }
        }
        """;

    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    private static CSharpCompilation Compile(params string[] sources) => CSharpCompilation.Create(
        "Generated",
        sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest))),
        References,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static GeneratorDriver Run(params string[] sources)
    {
        CSharpCompilation compilation = Compile(sources);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return CSharpGeneratorDriver.Create(new LeewayGenerator()).RunGenerators(compilation);
    }

    private static void AssertCompiles(params string[] sources)
    {
        CSharpCompilation compilation = Compile(sources);
        CSharpGeneratorDriver.Create(new LeewayGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning));
        Assert.True(output.SyntaxTrees.Count() > compilation.SyntaxTrees.Count());
    }

    [Fact]
    public Task A_client_entity_gets_a_schema_and_a_typed_api() => Verify(Run(ClientEntity));

    [Fact]
    public Task A_server_entity_gets_a_schema_only() => Verify(Run(ServerEntity));

    [Fact]
    public void A_client_entity_compiles_cleanly() => AssertCompiles(ClientEntity);

    [Fact]
    public void A_server_entity_compiles_cleanly() => AssertCompiles(ServerEntity);

    [Fact]
    public void An_internal_entity_gets_an_internal_api_that_compiles()
    {
        const string Source = """
            using Leeway.Core;
            using Leeway.Datasync.Client;
            namespace Venue;
            internal class Hidden : OfflineEntity { [Counter] public int Count { get; set; } }
            """;
        AssertCompiles(Source);
        string generated = Run(Source).GetRunResult().GeneratedTrees.Single().ToString();
        Assert.Contains("internal static class HiddenLeewayExtensions", generated);
    }

    [Fact]
    public void An_entity_with_no_namespace_compiles()
        => AssertCompiles("using Leeway.Core; using Leeway.Datasync.Client; public class Top : OfflineEntity { [Counter] public int Count { get; set; } }");

    [Fact]
    public void A_nested_entity_gets_a_schema_but_no_typed_api()
    {
        const string Source = """
            using Leeway.Core;
            using Leeway.Datasync.Client;
            namespace Venue;
            public class Outer { public class Inner : OfflineEntity { [Counter] public int Count { get; set; } } }
            """;
        AssertCompiles(Source);
        string generated = Run(Source).GetRunResult().GeneratedTrees.Single().ToString();
        Assert.Contains("MergeField.Of<global::Venue.Outer.Inner", generated);
        Assert.DoesNotContain("Extensions", generated);
    }

    [Fact]
    public void An_entity_without_a_public_parameterless_constructor_gets_no_edit_methods_but_still_compiles()
    {
        const string Source = """
            using Leeway.Core;
            using Leeway.Datasync.Client;
            namespace Venue;
            public class NoCtor : OfflineEntity
            {
                public NoCtor(int x) { }
                [Counter] public int Count { get; set; }
                [Escrow] public int Stock { get; set; }
            }
            """;
        AssertCompiles(Source);
        string generated = Run(Source).GetRunResult().GeneratedTrees.Single().ToString();
        Assert.DoesNotContain("AddCountAsync", generated);
        Assert.Contains("TrySpendStockAsync", generated);
    }

    [Fact]
    public void Inherited_merge_fields_are_included()
    {
        const string Source = """
            using Leeway.Core;
            using Leeway.Datasync.Client;
            namespace Venue;
            public abstract class Base : OfflineEntity { [Counter] public int Shared { get; set; } }
            public class Derived : Base { [Counter] public int Own { get; set; } }
            """;
        AssertCompiles(Source);
        string derived = Run(Source).GetRunResult().GeneratedTrees.Single(t => t.FilePath.Contains("Derived")).ToString();
        Assert.Contains("\"Shared\"", derived);
        Assert.Contains("\"Own\"", derived);
    }

    [Theory]
    [InlineData("public class Plain { public int Count { get; set; } }")]
    [InlineData("using Leeway.Datasync.Client; public class NoAttributes : OfflineEntity { public int Count { get; set; } }")]
    [InlineData("using Leeway.Core; public class Generic<T> : Leeway.Datasync.Client.OfflineEntity { [Counter] public int Count { get; set; } }")]
    public void Nothing_is_generated_when_there_is_nothing_to_do(string source)
        => Assert.Empty(Run(source).GetRunResult().GeneratedTrees);

    /// <summary>Each declaration of a partial class reaches the transform, and they must not produce two files.</summary>
    [Fact]
    public void A_partial_class_with_several_declarations_is_generated_once()
    {
        const string Source = """
            using Leeway.Core;
            using Leeway.Datasync.Client;
            namespace Venue;
            public partial class Split : OfflineEntity { [Counter] public int One { get; set; } }
            public partial class Split { [Counter] public int Two { get; set; } }
            """;
        AssertCompiles(Source);
        Assert.Single(Run(Source).GetRunResult().GeneratedTrees);
    }

    [Fact]
    public void An_unrelated_edit_does_no_new_work()
    {
        CSharpCompilation first = Compile(ClientEntity, "public class Unrelated { }");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new LeewayGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(first);

        SyntaxTree unrelated = first.SyntaxTrees.Last();
        CSharpCompilation second = first.ReplaceSyntaxTree(unrelated, CSharpSyntaxTree.ParseText("public class Unrelated { public int Changed; }", new CSharpParseOptions(LanguageVersion.Latest)));
        GeneratorRunResult result = driver.RunGenerators(second).GetRunResult().Results.Single();

        IncrementalGeneratorRunStep[] outputs = result.TrackedOutputSteps.SelectMany(s => s.Value).ToArray();
        Assert.NotEmpty(outputs);
        Assert.All(outputs.SelectMany(step => step.Outputs), output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
    }
}
