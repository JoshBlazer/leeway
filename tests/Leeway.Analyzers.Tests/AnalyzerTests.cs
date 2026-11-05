using Leeway.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Leeway.Analyzers.Tests;

/// <summary>
/// The harness's default reference assemblies are downloaded from NuGet at test time into a shared temp folder,
/// which made tests fail intermittently. Compiling against the running runtime needs no network.
/// </summary>
internal static class OfflineReferences
{
    public static void Apply(AnalyzerTest<DefaultVerifier> test)
    {
        test.ReferenceAssemblies = new ReferenceAssemblies("net10.0");
        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string[] platformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        foreach (string assembly in platformAssemblies.Where(a => Path.GetDirectoryName(a) == runtimeDirectory))
        {
            test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(assembly));
        }

        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(Leeway.Core.CounterAttribute).Assembly.Location));
        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(Leeway.Datasync.Client.OfflineEntity).Assembly.Location));
        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(Leeway.Datasync.Server.LeewayEntity).Assembly.Location));
        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(CommunityToolkit.Datasync.Server.EntityFrameworkCore.BaseEntityTableData).Assembly.Location));
        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(CommunityToolkit.Datasync.Server.ITableData).Assembly.Location));
    }
}

/// <summary>M5 exit check: analyzer diagnostics.</summary>
public class AnalyzerTests
{
    private const string Header = """
        using Leeway.Core;
        using Leeway.Datasync.Client;
        using Leeway.Datasync.Server;

        """;

    private static Task Check(string source)
    {
        CSharpAnalyzerTest<LeewayAnalyzer, DefaultVerifier> test = new() { TestCode = Header + source };
        OfflineReferences.Apply(test);
        return test.RunAsync();
    }

    // ---------- LEE001 ----------

    [Theory]
    [InlineData("z.Stock--;")]
    [InlineData("--z.Stock;")]
    [InlineData("z.Stock -= 2;")]
    [InlineData("z.Stock = z.Stock - 1;")]
    public Task LEE001_flags_a_decremented_numeric_entity_field_with_no_rule(string statement) => Check($$"""
        public class Item : OfflineEntity { public int Stock { get; set; } }
        public class C { void M(Item z) { {|LEE001:{{statement.TrimEnd(';')}}|}; } }
        """);

    [Fact]
    public Task LEE001_applies_to_server_entities_too() => Check("""
        public class Item : LeewayEntity { public decimal Balance { get; set; } }
        public class C { void M(Item z) { {|LEE001:z.Balance -= 1|}; } }
        """);

    [Theory]
    [InlineData("[Escrow] public int Stock { get; set; }", "z.Stock++")]
    [InlineData("public int Stock { get; set; }", "z.Stock++")]
    [InlineData("public int Stock { get; set; }", "z.Stock += 2")]
    [InlineData("public int Stock { get; set; }", "z.Stock = z.Other - 1")]
    public Task LEE001_ignores_fields_with_a_rule_and_things_that_are_not_decrements(string property, string statement)
    {
        string body = property.StartsWith("[Escrow]", StringComparison.Ordinal) ? $"{{|LEE002:{statement}|}}" : statement;
        return Check($$"""
            public class Item : OfflineEntity { {{property}} public int Other { get; set; } }
            public class C { void M(Item z) { {{body}}; } }
            """);
    }

    [Fact]
    public Task LEE001_ignores_classes_that_are_not_entities() => Check("""
        public class Plain { public int Stock { get; set; } }
        public class C { void M(Plain z) { z.Stock--; } }
        """);

    [Fact]
    public Task LEE001_ignores_a_string_field_and_a_local() => Check("""
        public class Item : OfflineEntity { public string? Name { get; set; } }
        public class C { void M() { int x = 1; x--; } }
        """);

    // ---------- LEE002 ----------

    [Theory]
    [InlineData("[LastWriterWins] public string? Notes { get; set; }", "z.Notes = \"x\"")]
    [InlineData("[Counter] public int Notes { get; set; }", "z.Notes++")]
    [InlineData("[Counter] public int Notes { get; set; }", "z.Notes -= 1")]
    [InlineData("[Escrow] public int Notes { get; set; }", "z.Notes = 3")]
    [InlineData("[AddWinsSet] public string? Notes { get; set; }", "z.Notes = null")]
    public Task LEE002_flags_a_direct_write_to_a_merged_field(string property, string statement) => Check($$"""
        public class Item : OfflineEntity { {{property}} }
        public class C { void M(Item z) { {|LEE002:{{statement}}|}; } }
        """);

    [Fact]
    public Task LEE002_allows_an_object_initializer_and_reads() => Check("""
        public class Item : LeewayEntity { [Counter] public int Count { get; set; } [LastWriterWins] public string? Notes { get; set; } }
        public class C { Item M(Item z) { int n = z.Count; string? s = z.Notes; return new Item { Count = n, Notes = s }; } }
        """);

    // ---------- LEE003 ----------

    [Theory]
    [InlineData("[{|LEE003:Counter|}] public string? X { get; set; }")]
    [InlineData("[{|LEE003:Escrow|}] public decimal X { get; set; }")]
    [InlineData("[{|LEE003:AddWinsSet|}] public int X { get; set; }")]
    [InlineData("[{|LEE003:Counter|}] public int X { get; }")]
    [InlineData("[{|LEE003:LastWriterWins|}] public int X { get; private set; }")]
    public Task LEE003_flags_an_unsuitable_property(string property) => Check($$"""
        public class Item : OfflineEntity { {{property}} }
        """);

    [Theory]
    [InlineData("[Counter] public long X { get; set; }")]
    [InlineData("[Escrow] public int X { get; set; }")]
    [InlineData("[AddWinsSet] public string? X { get; set; }")]
    [InlineData("[LastWriterWins] public System.DateTimeOffset? X { get; set; }")]
    public Task LEE003_accepts_a_suitable_property(string property) => Check($$"""
        public class Item : OfflineEntity { {{property}} }
        """);

    // ---------- LEE004 ----------

    [Fact]
    public Task LEE004_flags_two_merge_attributes_on_a_property() => Check("""
        public class Item : OfflineEntity { [{|LEE004:Counter|}] [Escrow] public int X { get; set; } }
        """);

    // ---------- LEE005 ----------

    [Fact]
    public Task LEE005_flags_a_merge_attribute_outside_an_entity() => Check("""
        public class Plain { [{|LEE005:Counter|}] public int X { get; set; } }
        """);

    // ---------- LEE006 ----------

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public Task LEE006_flags_a_lease_that_grants_nothing(string lease) => Check($$"""
        public class Item : OfflineEntity { [{|LEE006:Escrow(Lease = {{lease}})|}] public int X { get; set; } }
        """);

    [Fact]
    public Task LEE006_accepts_a_positive_lease() => Check("""
        public class Item : OfflineEntity { [Escrow(Lease = 1)] public int X { get; set; } }
        """);

    /// <summary>Guards against the harness silently compiling nothing: broken code must be reported.</summary>
    [Fact]
    public async Task The_harness_really_compiles_the_test_code()
        => await Assert.ThrowsAnyAsync<Exception>(() => Check("public class C { public NoSuchType F; }"));
}
