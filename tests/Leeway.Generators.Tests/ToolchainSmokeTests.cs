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

/// <summary>A test-only generator that emits real source, so the snapshot below has something to compare.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class SmokeGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterPostInitializationOutput(output => output.AddSource("Smoke.g.cs", "public static class Smoke { public const int Answer = 42; }"));
}

/// <summary>Proves only that the generator driver and Verify snapshots work. Real snapshots arrive in M5.</summary>
public class ToolchainSmokeTests
{
    private static GeneratorDriver Run(IIncrementalGenerator generator)
    {
        CSharpCompilation compilation = CSharpCompilation.Create("Smoke", [CSharpSyntaxTree.ParseText("public class C;")]);
        return CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);
    }

    [Fact]
    public Task Generated_source_is_compared_against_a_committed_snapshot() => Verify(Run(new SmokeGenerator()));

    [Fact]
    public void Scaffold_generator_emits_nothing() => Assert.Empty(Run(new LeewayGenerator()).GetRunResult().GeneratedTrees);
}
