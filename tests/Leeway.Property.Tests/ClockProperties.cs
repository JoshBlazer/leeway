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
