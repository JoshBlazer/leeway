using System.Collections.Immutable;

namespace Leeway.Core;

/// <summary>A point in hybrid logical time. The order is total, so every replica ranks two stamps the same way.</summary>
/// <param name="Physical">Milliseconds since the Unix epoch.</param>
/// <param name="Logical">Orders stamps sharing a physical time.</param>
/// <param name="Replica">The replica that made the stamp, the final tie-break.</param>
public readonly record struct HybridTimestamp(long Physical, int Logical, string Replica) : IComparable<HybridTimestamp>
{
    /// <inheritdoc />
    public int CompareTo(HybridTimestamp other)
    {
        int result = Physical.CompareTo(other.Physical);
        if (result == 0)
        {
            result = Logical.CompareTo(other.Logical);
        }

        return result != 0 ? result : string.CompareOrdinal(Replica ?? string.Empty, other.Replica ?? string.Empty);
    }
}

/// <summary>A hybrid logical clock. See docs/design/hlc.md.</summary>
public sealed class HybridClock
{
    private readonly string replica;
    private readonly Func<long> wallClock;

    /// <summary>Creates a clock, optionally resuming from a persisted stamp.</summary>
    /// <param name="replica">The owning replica.</param>
    /// <param name="wallClock">Supplies the current wall time in Unix milliseconds. It may be wrong.</param>
    /// <param name="last">The last stamp issued or observed, if resuming.</param>
    public HybridClock(string replica, Func<long> wallClock, HybridTimestamp? last = null)
    {
        this.replica = replica;
        this.wallClock = wallClock;
        Last = last ?? new HybridTimestamp(0, 0, replica);
    }

    /// <summary>The highest stamp issued or observed.</summary>
    public HybridTimestamp Last { get; private set; }

    /// <summary>Issues a stamp strictly later than everything issued or observed.</summary>
    /// <returns>The new stamp.</returns>
    public HybridTimestamp Now()
    {
        long wall = this.wallClock();
        Last = wall > Last.Physical
            ? new HybridTimestamp(wall, 0, this.replica)
            : new HybridTimestamp(Last.Physical, Last.Logical + 1, this.replica);
        return Last;
    }

    /// <summary>Records a stamp seen from elsewhere, so that later stamps sort after it.</summary>
    /// <param name="seen">The stamp seen.</param>
    /// <returns>The clock's new position.</returns>
    public HybridTimestamp Observe(HybridTimestamp seen)
    {
        if (seen.CompareTo(Last) > 0)
        {
            Last = new HybridTimestamp(seen.Physical, seen.Logical, this.replica);
        }

        return Last;
    }
}

/// <summary>
/// Gives each operation its canonical stamp on the server, bounding the influence of a wrong device clock
/// while keeping causal order. See docs/design/hlc.md.
/// </summary>
/// <param name="MaxDriftMs">How far ahead of the server a stamp may be.</param>
/// <param name="Offsets">The correction in force for each device.</param>
public sealed record StampAuthority(long MaxDriftMs, ImmutableSortedDictionary<string, long> Offsets)
{
    /// <summary>The default drift limit: five minutes.</summary>
    public const long DefaultMaxDriftMs = 300_000;

    /// <summary>Creates an authority with no offsets.</summary>
    /// <param name="maxDriftMs">How far ahead of the server a stamp may be.</param>
    /// <returns>The new authority.</returns>
    public static StampAuthority Create(long maxDriftMs = DefaultMaxDriftMs) => new(maxDriftMs, Maps.Empty<long>());

    /// <summary>Computes an operation's canonical stamp.</summary>
    /// <param name="op">The operation.</param>
    /// <param name="nowMs">The server's time.</param>
    /// <returns>The canonical stamp, and the authority's next state.</returns>
    public (HybridTimestamp Canonical, StampAuthority Next) Admit(Operation op, long nowMs)
    {
        long offset = Offsets.GetValueOrDefault(op.Device);
        long limit = nowMs + MaxDriftMs;
        long shifted = op.Stamp.Physical - offset;
        StampAuthority next = this;
        if (shifted > limit)
        {
            offset += shifted - limit;
            shifted = limit;
            next = this with { Offsets = Maps.Normal(Offsets).SetItem(op.Device, offset) };
        }

        long physical = Math.Max(shifted, op.Observed.Physical + 1);
        return (new HybridTimestamp(physical, (int)Math.Min(op.Sequence, int.MaxValue), op.Device), next);
    }

    /// <inheritdoc />
    public bool Equals(StampAuthority? other)
        => other is not null && MaxDriftMs == other.MaxDriftMs && Maps.SameItems(Offsets, other.Offsets, EqualityComparer<long>.Default);

    /// <inheritdoc />
    public override int GetHashCode() => MaxDriftMs.GetHashCode() ^ Offsets.Count;
}
