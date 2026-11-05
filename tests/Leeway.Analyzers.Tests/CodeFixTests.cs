using Leeway.Analyzers;
using Leeway.Analyzers.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Leeway.Analyzers.Tests;

/// <summary>M5 exit check: code fixes.</summary>
public class CodeFixTests
{
    private static Task Fix(string source, string expected, string equivalenceKey)
    {
        CSharpCodeFixTest<LeewayAnalyzer, AddMergeRuleCodeFix, DefaultVerifier> test = new()
        {
            TestCode = source,
            FixedCode = expected,
            CodeActionEquivalenceKey = equivalenceKey,
        };
        OfflineReferences.Apply(test);
        test.FixedState.AdditionalReferences.AddRange(test.TestState.AdditionalReferences);
        return test.RunAsync();
    }

    [Fact]
    public Task Adds_an_escrow_attribute() => Fix(
        """
        using Leeway.Core;
        using Leeway.Datasync.Client;

        public class Item : OfflineEntity
        {
            public int Stock { get; set; }
        }

        public class C
        {
            void M(Item z) { {|LEE001:z.Stock--|}; }
        }
        """,
        """
        using Leeway.Core;
        using Leeway.Datasync.Client;

        public class Item : OfflineEntity
        {
            [Escrow(Min = 0, Lease = 1)]
            public int Stock { get; set; }
        }

        public class C
        {
            void M(Item z) { {|LEE002:z.Stock--|}; }
        }
        """,
        AddMergeRuleCodeFix.EscrowKey);

    [Fact]
    public Task Adds_a_counter_attribute() => Fix(
        """
        using Leeway.Core;
        using Leeway.Datasync.Client;

        public class Item : OfflineEntity
        {
            public int Stock { get; set; }
        }

        public class C
        {
            void M(Item z) { {|LEE001:z.Stock -= 2|}; }
        }
        """,
        """
        using Leeway.Core;
        using Leeway.Datasync.Client;

        public class Item : OfflineEntity
        {
            [Counter]
            public int Stock { get; set; }
        }

        public class C
        {
            void M(Item z) { {|LEE002:z.Stock -= 2|}; }
        }
        """,
        AddMergeRuleCodeFix.CounterKey);

    [Fact]
    public Task Adds_the_using_directive_if_it_is_missing() => Fix(
        """
        using Leeway.Datasync.Client;

        public class Item : OfflineEntity
        {
            public int Stock { get; set; }

            public void Take() { {|LEE001:Stock--|}; }
        }
        """,
        """
        using Leeway.Core;
        using Leeway.Datasync.Client;

        public class Item : OfflineEntity
        {
            [Escrow(Min = 0, Lease = 1)]
            public int Stock { get; set; }

            public void Take() { {|LEE002:Stock--|}; }
        }
        """,
        AddMergeRuleCodeFix.EscrowKey);

    [Fact]
    public Task Keeps_existing_attributes_and_comments() => Fix(
        """
        using Leeway.Core;
        using Leeway.Datasync.Client;

        public class Item : OfflineEntity
        {
            /// <summary>How many are left.</summary>
            [System.Obsolete]
            public int Stock { get; set; }

        #pragma warning disable CS0612
            void Take() { {|LEE001:Stock--|}; }
        }
        """,
        """
        using Leeway.Core;
        using Leeway.Datasync.Client;

        public class Item : OfflineEntity
        {
            /// <summary>How many are left.</summary>
            [System.Obsolete]
            [Escrow(Min = 0, Lease = 1)]
            public int Stock { get; set; }

        #pragma warning disable CS0612
            void Take() { {|LEE002:Stock--|}; }
        }
        """,
        AddMergeRuleCodeFix.EscrowKey);

    [Fact]
    public Task Fixes_a_property_declared_in_another_file()
    {
        CSharpCodeFixTest<LeewayAnalyzer, AddMergeRuleCodeFix, DefaultVerifier> test = new() { CodeActionEquivalenceKey = AddMergeRuleCodeFix.CounterKey };
        test.TestState.Sources.Add(("Item.cs", """
            using Leeway.Core;
            using Leeway.Datasync.Client;

            public class Item : OfflineEntity
            {
                public int Stock { get; set; }
            }
            """));
        test.TestState.Sources.Add(("Use.cs", "public class C { void M(Item z) { {|LEE001:z.Stock--|}; } }"));
        test.FixedState.Sources.Add(("Item.cs", """
            using Leeway.Core;
            using Leeway.Datasync.Client;

            public class Item : OfflineEntity
            {
                [Counter]
                public int Stock { get; set; }
            }
            """));
        test.FixedState.Sources.Add(("Use.cs", "public class C { void M(Item z) { {|LEE002:z.Stock--|}; } }"));
        OfflineReferences.Apply(test);
        test.FixedState.AdditionalReferences.AddRange(test.TestState.AdditionalReferences);
        return test.RunAsync();
    }
}
