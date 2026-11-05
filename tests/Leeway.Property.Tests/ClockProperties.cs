using FsCheck.Xunit;
using Leeway.Core;

namespace Leeway.Property.Tests;

public class ClockProperties
{
    private static Operation Op(string device, long sequence, long physical, long observed = 0)
        => new($"{device}-{sequence}", device, sequence, new HybridTimestamp(physical, 0, device), new HybridTimestamp(observed, 0, "server"), OperationKind.Merge, "T", "e");

    [Property]
    public bool Now_is_strictly_increasing_whatever_the_wall_clock_does(long[] wallTimes)
    {
        long wall = 0;
        HybridClock clock = new("a", () => wall);
        HybridTimestamp last = clock.Last;
        foreach (long t in wallTimes ?? [])
        {
            wall = Math.Abs(t % 1_000_000);
            HybridTimestamp now = clock.Now();
            if (now.CompareTo(last) <= 0)
            {
                return false;
            }

            last = now;
        }

        return true;
    }

    [Property]
    public bool After_observing_a_stamp_the_next_stamp_is_later(int wall, int remotePhysical, byte remoteLogical)
    {
        HybridClock clock = new("a", () => Math.Abs((long)wall));
        HybridTimestamp remote = new(Math.Abs((long)remotePhysical), remoteLogical, "b");
        clock.Observe(remote);
        return clock.Now().CompareTo(remote) > 0;
    }

    [Property]
    public bool A_device_clock_ahead_gets_no_more_than_the_drift_limit(int ahead)
    {
        const long Now = 1_000_000;
        const long MaxDrift = 5_000;
        (HybridTimestamp canonical, _) = StampAuthority.Create(MaxDrift).Admit(Op("fast", 1, Now + Math.Abs((long)ahead)), Now);
        return canonical.Physical <= Now + MaxDrift;
    }

    [Property]
    public bool A_write_that_saw_a_stamp_always_beats_it(int deviceClock, int observed)
    {
        const long Now = 1_000_000;
        long seen = Math.Abs((long)observed) % (2 * Now);
        (HybridTimestamp canonical, _) = StampAuthority.Create(5_000).Admit(Op("slow", 1, Math.Abs((long)deviceClock) % Now, seen), Now);
        return canonical.CompareTo(new HybridTimestamp(seen, int.MaxValue, "zzz")) > 0;
    }

    [Property]
    public bool A_devices_later_write_beats_its_earlier_one_in_either_arrival_order(ushort gap, bool laterFirst, int ahead)
    {
        const long Now = 1_000_000;
        long start = Now + Math.Abs((long)ahead);
        Operation earlier = Op("d", 1, start);
        Operation later = Op("d", 2, start + gap);

        StampAuthority authority = StampAuthority.Create(5_000);
        HybridTimestamp a;
        HybridTimestamp b;
        if (laterFirst)
        {
            (b, authority) = authority.Admit(later, Now);
            (a, _) = authority.Admit(earlier, Now + 10);
        }
        else
        {
            (a, authority) = authority.Admit(earlier, Now);
            (b, _) = authority.Admit(later, Now + 10);
        }

        return b.CompareTo(a) > 0;
    }

    /// <summary>
    /// The general form of the rule. Two earlier designs passed the two-operation test above and failed this,
    /// and the simulator is what found it: a late arrival must land between its neighbours in sequence order.
    /// </summary>
    [Property(MaxTest = 500)]
    public bool A_devices_stamps_stay_in_sequence_order_for_any_arrival_order_and_times(byte count, int shuffle, int ahead, ushort[] gaps)
    {
        const long Start = 1_000_000;
        int total = 2 + (count % 12);
        long clock = Start + Math.Abs((long)ahead) % 1_000_000;
        Operation[] ops = Enumerable.Range(1, total).Select(i => Op("d", i, clock + (i * 7))).ToArray();

        Random random = new(shuffle);
        Dictionary<long, HybridTimestamp> canonical = [];
        StampAuthority authority = StampAuthority.Create(5_000);
        long now = Start;
        int step = 0;
        foreach (Operation op in ops.OrderBy(_ => random.Next()))
        {
            now += gaps is { Length: > 0 } ? gaps[step++ % gaps.Length] : 1;
            (canonical[op.Sequence], authority) = authority.Admit(op, now);
        }

        return Enumerable.Range(1, total - 1).All(i => canonical[i + 1].CompareTo(canonical[i]) > 0);
    }

    /// <summary>The list of neighbours is bounded by what is unacknowledged, not by history.</summary>
    [Fact]
    public void Settled_sequences_are_pruned()
    {
        StampAuthority authority = StampAuthority.Create();
        for (int i = 1; i <= 100; i++)
        {
            (_, authority) = authority.Admit(Op("d", i, 1_000 + i) with { SettledBelow = i }, 1_000_000);
        }

        Assert.True(authority.Admitted["d"].Count <= 2, $"Kept {authority.Admitted["d"].Count} entries.");
    }

    [Fact]
    public void An_authority_survives_a_json_round_trip()
    {
        (_, StampAuthority authority) = StampAuthority.Create().Admit(Op("d", 3, 1_000), 1_000_000);
        Assert.Equal(authority, StampAuthority.FromJson(authority.ToJson()));
    }

    [Fact]
    public void A_device_a_day_fast_cannot_beat_a_correct_later_writer()
    {
        const long Day = 86_400_000;
        const long Now = 10 * Day;
        StampAuthority authority = StampAuthority.Create(300_000);

        (HybridTimestamp fast, authority) = authority.Admit(Op("fast", 1, Now + Day), Now);
        long later = Now + 400_000;
        (HybridTimestamp honest, _) = authority.Admit(Op("honest", 1, later), later);

        Assert.True(honest.CompareTo(fast) > 0);
    }
}
