using Leeway.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Leeway.Analyzers.Tests;

/// <summary>
/// The harness's default reference assemblies are downloaded from NuGet at test time into a shared temp folder,
/// which made tests fail intermittently. Compiling against the running runtime needs no network.
/// </summary>
internal static class OfflineReferences
{
    public static void Apply<TAnalyzer>(CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> test)
        where TAnalyzer : Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer, new()
    {
        test.ReferenceAssemblies = new ReferenceAssemblies("net10.0");

        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string[] platformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        foreach (string assembly in platformAssemblies.Where(a => Path.GetDirectoryName(a) == runtimeDirectory))
        {
            test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(assembly));
        }
    }
}

/// <summary>Proves only that the Roslyn analyzer test harness works. Real diagnostics arrive in M5.</summary>
public class ToolchainSmokeTests
{
    [Fact]
    public async Task Scaffold_analyzer_reports_nothing()
    {
        CSharpAnalyzerTest<LeewayAnalyzer, DefaultVerifier> test = new() { TestCode = "public class C { public string S => new object().ToString()!; }" };
        OfflineReferences.Apply(test);
        await test.RunAsync();
    }

    /// <summary>Guards against the harness silently compiling nothing: broken code must be reported.</summary>
    [Fact]
    public async Task The_harness_really_compiles_the_test_code()
    {
        CSharpAnalyzerTest<LeewayAnalyzer, DefaultVerifier> test = new() { TestCode = "public class C { public NoSuchType F; }" };
        OfflineReferences.Apply(test);
        await Assert.ThrowsAnyAsync<Exception>(() => test.RunAsync());
    }
}
