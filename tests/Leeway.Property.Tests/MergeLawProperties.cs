using FsCheck;
using FsCheck.Xunit;
using Leeway.Core;

namespace Leeway.Property.Tests;

/// <summary>M1 exit check: merge laws. Replicas are built from arbitrary operation lists, so states are reachable ones.</summary>
public class MergeLawProperties
{
    private static string Replica(byte b) => "r" + (b % 4);

    private static PnCounter Counter((byte Replica, sbyte Amount)[] ops)
    {
        PnCounter state = PnCounter.Empty;
        foreach ((byte replica, sbyte amount) in ops ?? [])
        {
            state = state.Merge(state.AddDelta(Replica(replica), amount));
        }

        return state;
    }

    private static LwwRegister Register((byte Physical, byte Logical, byte Replica) op)
        => new($"v{op.Physical}-{op.Logical}-{op.Replica % 4}", new HybridTimestamp(op.Physical, op.Logical, Replica(op.Replica)));

    private static OrSet Set(string tagPrefix, (byte Element, bool Add)[] ops)
    {
        OrSet state = OrSet.Empty;
        int i = 0;
        foreach ((byte element, bool add) in ops ?? [])
        {
            string name = "e" + (element % 4);
            state = state.Merge(add ? state.AddDelta(name, $"{tagPrefix}:{i++}") : state.RemoveDelta(name));
        }

        return state;
    }

    private static MergeDocument Doc(string tagPrefix, (byte Replica, sbyte Amount)[] counter, (byte, byte, byte) register, (byte Element, bool Add)[] set)
        => MergeDocument.Empty.WithCounter("c", Counter(counter)).WithRegister("r", Register(register)).WithSet("s", Set(tagPrefix, set));

    [Property]
    public bool Counter_merge_is_commutative((byte, sbyte)[] a, (byte, sbyte)[] b)
        => Counter(a).Merge(Counter(b)).Equals(Counter(b).Merge(Counter(a)));

    [Property]
    public bool Counter_merge_is_associative((byte, sbyte)[] a, (byte, sbyte)[] b, (byte, sbyte)[] c)
        => Counter(a).Merge(Counter(b)).Merge(Counter(c)).Equals(Counter(a).Merge(Counter(b).Merge(Counter(c))));

    [Property]
    public bool Counter_merge_is_idempotent((byte, sbyte)[] a)
        => Counter(a).Merge(Counter(a)).Equals(Counter(a));

    [Property]
    public bool Counter_value_is_the_sum_of_every_replicas_operations(sbyte[] left, sbyte[] right)
    {
        PnCounter one = PnCounter.Empty;
        foreach (sbyte amount in left ?? [])
        {
            one = one.Merge(one.AddDelta("left", amount));
        }

        PnCounter two = PnCounter.Empty;
        foreach (sbyte amount in right ?? [])
        {
            two = two.Merge(two.AddDelta("right", amount));
        }

        long expected = (left ?? []).Sum(x => (long)x) + (right ?? []).Sum(x => (long)x);
        return one.Merge(two).Value == expected;
    }

    [Property]
    public bool Counter_replaying_a_delta_changes_nothing((byte, sbyte)[] a, byte replica, sbyte amount)
    {
        PnCounter delta = Counter(a).AddDelta(Replica(replica), amount);
        PnCounter once = Counter(a).Merge(delta);
        return once.Merge(delta).Equals(once);
    }

    [Property]
    public bool Register_merge_is_commutative((byte, byte, byte) a, (byte, byte, byte) b)
        => Register(a).Merge(Register(b)).Equals(Register(b).Merge(Register(a)));

    [Property]
    public bool Register_merge_is_associative((byte, byte, byte) a, (byte, byte, byte) b, (byte, byte, byte) c)
        => Register(a).Merge(Register(b)).Merge(Register(c)).Equals(Register(a).Merge(Register(b).Merge(Register(c))));

    [Property]
    public bool Register_merge_is_idempotent((byte, byte, byte) a)
        => Register(a).Merge(Register(a)).Equals(Register(a));

    [Property]
    public bool Set_merge_is_commutative((byte, bool)[] a, (byte, bool)[] b)
        => Set("a", a).Merge(Set("b", b)).Equals(Set("b", b).Merge(Set("a", a)));

    [Property]
    public bool Set_merge_is_associative((byte, bool)[] a, (byte, bool)[] b, (byte, bool)[] c)
        => Set("a", a).Merge(Set("b", b)).Merge(Set("c", c)).Equals(Set("a", a).Merge(Set("b", b).Merge(Set("c", c))));

    [Property]
    public bool Set_merge_is_idempotent((byte, bool)[] a)
        => Set("a", a).Merge(Set("a", a)).Equals(Set("a", a));

    [Fact]
    public void Set_a_concurrent_add_beats_a_remove()
    {
        OrSet shared = OrSet.Empty.Merge(OrSet.Empty.AddDelta("vip", "seed:0"));
        OrSet removed = shared.Merge(shared.RemoveDelta("vip"));
        OrSet added = shared.Merge(shared.AddDelta("vip", "other:0"));

        Assert.Empty(removed.Elements);
        Assert.Contains("vip", removed.Merge(added).Elements);
        Assert.Contains("vip", added.Merge(removed).Elements);
    }

    [Fact]
    public void Set_a_remove_that_saw_the_add_wins()
    {
        OrSet state = OrSet.Empty.Merge(OrSet.Empty.AddDelta("vip", "seed:0"));
        OrSet removed = state.Merge(state.RemoveDelta("vip"));
        Assert.Empty(removed.Merge(state).Elements);
    }

    [Property]
    public bool Document_merge_is_commutative((byte, sbyte)[] c1, (byte, byte, byte) r1, (byte, bool)[] s1, (byte, sbyte)[] c2, (byte, byte, byte) r2, (byte, bool)[] s2)
        => Doc("a", c1, r1, s1).Merge(Doc("b", c2, r2, s2)).Equals(Doc("b", c2, r2, s2).Merge(Doc("a", c1, r1, s1)));

    [Property]
    public bool Document_merge_is_associative((byte, sbyte)[] c1, (byte, byte, byte) r1, (byte, bool)[] s1, (byte, sbyte)[] c2, (byte, byte, byte) r2, (byte, bool)[] s2, (byte, sbyte)[] c3, (byte, byte, byte) r3, (byte, bool)[] s3)
    {
        MergeDocument a = Doc("a", c1, r1, s1);
        MergeDocument b = Doc("b", c2, r2, s2);
        MergeDocument c = Doc("c", c3, r3, s3);
        return a.Merge(b).Merge(c).Equals(a.Merge(b.Merge(c)));
    }

    [Property]
    public bool Document_merge_is_idempotent((byte, sbyte)[] c1, (byte, byte, byte) r1, (byte, bool)[] s1)
        => Doc("a", c1, r1, s1).Merge(Doc("a", c1, r1, s1)).Equals(Doc("a", c1, r1, s1));

    [Property]
    public bool Document_survives_a_json_round_trip((byte, sbyte)[] c1, (byte, byte, byte) r1, (byte, bool)[] s1)
    {
        MergeDocument doc = Doc("a", c1, r1, s1);
        MergeDocument back = MergeDocument.FromJson(doc.ToJson());
        return back.Equals(doc) && back.Merge(doc).Equals(doc);
    }
}
