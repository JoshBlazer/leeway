using Leeway.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Leeway.Analyzers.Tests;

/// <summary>Proves only that the Roslyn analyzer test harness works. Real diagnostics arrive in M5.</summary>
public class ToolchainSmokeTests
{
    [Fact]
    public async Task Scaffold_analyzer_reports_nothing()
    {
        CSharpAnalyzerTest<LeewayAnalyzer, DefaultVerifier> test = new() { TestCode = "public class C { }" };
        await test.RunAsync();
    }
}
