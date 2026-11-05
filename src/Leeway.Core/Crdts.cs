using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Leeway.Core;

internal static class Maps
{
    public static ImmutableSortedDictionary<string, T> Empty<T>()
        => ImmutableSortedDictionary<string, T>.Empty.WithComparers(StringComparer.Ordinal);

    public static ImmutableSortedSet<string> EmptySet() => ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    /// <summary>JSON deserialization loses the ordinal comparer, which determinism depends on.</summary>
    public static ImmutableSortedDictionary<string, T> Normal<T>(ImmutableSortedDictionary<string, T>? map)
        => (map ?? Empty<T>()).WithComparers(StringComparer.Ordinal);

    public static ImmutableSortedSet<string> Normal(ImmutableSortedSet<string>? set)
        => (set ?? EmptySet()).WithComparer(StringComparer.Ordinal);

    public static ImmutableSortedDictionary<string, T> Union<T>(ImmutableSortedDictionary<string, T> a, ImmutableSortedDictionary<string, T> b, Func<T, T, T> combine)
    {
        ImmutableSortedDictionary<string, T>.Builder result = Normal(a).ToBuilder();
        foreach (KeyValuePair<string, T> item in b)
        {
            result[item.Key] = result.TryGetValue(item.Key, out T? existing) ? combine(existing, item.Value) : item.Value;
        }

        return result.ToImmutable();
    }

    public static bool SameItems<T>(ImmutableSortedDictionary<string, T> a, ImmutableSortedDictionary<string, T> b, IEqualityComparer<T> comparer)
        => a.Count == b.Count && a.All(item => b.TryGetValue(item.Key, out T? other) && comparer.Equals(item.Value, other));
}

/// <summary>A counter that adds and subtracts, with per-replica cumulative totals. See docs/design/merge-semantics.md.</summary>
/// <param name="Increments">Each replica's total of additions.</param>
/// <param name="Decrements">Each replica's total of subtractions.</param>
public sealed record PnCounter(ImmutableSortedDictionary<string, long> Increments, ImmutableSortedDictionary<string, long> Decrements)
{
    /// <summary>A counter at zero.</summary>
    public static PnCounter Empty { get; } = new(Maps.Empty<long>(), Maps.Empty<long>());

    /// <summary>The counter's value.</summary>
    [JsonIgnore]
    public long Value => Increments.Values.Sum() - Decrements.Values.Sum();

    /// <summary>Builds a delta holding only this replica's new cumulative total.</summary>
    /// <param name="replica">The replica adding.</param>
    /// <param name="amount">The amount, which may be negative.</param>
    /// <returns>The delta, to be merged.</returns>
    public PnCounter AddDelta(string replica, long amount)
    {
        long up = Increments.GetValueOrDefault(replica);
        long down = Decrements.GetValueOrDefault(replica);
        return new PnCounter(
            Maps.Empty<long>().SetItem(replica, amount > 0 ? up + amount : up),
            Maps.Empty<long>().SetItem(replica, amount < 0 ? down - amount : down));
    }

    /// <summary>Merges by taking each replica's maximum.</summary>
    /// <param name="other">The other state.</param>
    /// <returns>The merged state.</returns>
    public PnCounter Merge(PnCounter other)
        => new(Maps.Union(Increments, other.Increments, Math.Max), Maps.Union(Decrements, other.Decrements, Math.Max));

    /// <inheritdoc />
    public bool Equals(PnCounter? other)
        => other is not null
            && Maps.SameItems(Without0(Increments), Without0(other.Increments), EqualityComparer<long>.Default)
            && Maps.SameItems(Without0(Decrements), Without0(other.Decrements), EqualityComparer<long>.Default);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    // A replica recorded at zero means the same as one not recorded.
    private static ImmutableSortedDictionary<string, long> Without0(ImmutableSortedDictionary<string, long> map)
        => map.RemoveRange(map.Where(x => x.Value == 0).Select(x => x.Key));
}

/// <summary>A last-writer-wins register. The value is JSON text.</summary>
/// <param name="Value">The value.</param>
/// <param name="Stamp">When it was written.</param>
public sealed record LwwRegister(string? Value, HybridTimestamp Stamp)
{
    /// <summary>Merges by keeping the write with the higher stamp.</summary>
    /// <param name="other">The other state.</param>
    /// <returns>The winner.</returns>
    public LwwRegister Merge(LwwRegister other)
    {
        int order = Stamp.CompareTo(other.Stamp);
        if (order == 0)
        {
            order = string.CompareOrdinal(Value, other.Value);
        }

        return order >= 0 ? this : other;
    }
}

/// <summary>An add-wins set: an observed-remove set of strings. See docs/design/merge-semantics.md.</summary>
/// <param name="Entries">The tags under which each element was added.</param>
/// <param name="Tombstones">The tags that have been removed.</param>
public sealed record OrSet(ImmutableSortedDictionary<string, ImmutableSortedSet<string>> Entries, ImmutableSortedSet<string> Tombstones)
{
    /// <summary>An empty set.</summary>
    public static OrSet Empty { get; } = new(Maps.Empty<ImmutableSortedSet<string>>(), Maps.EmptySet());

    /// <summary>The elements currently in the set.</summary>
    [JsonIgnore]
    public IEnumerable<string> Elements => Entries.Where(e => e.Value.Any(tag => !Tombstones.Contains(tag))).Select(e => e.Key);

    /// <summary>Builds a delta adding an element.</summary>
    /// <param name="element">The element.</param>
    /// <param name="tag">A globally unique tag for this add.</param>
    /// <returns>The delta, to be merged.</returns>
    public OrSet AddDelta(string element, string tag)
        => new(Maps.Empty<ImmutableSortedSet<string>>().SetItem(element, Maps.EmptySet().Add(tag)), Maps.EmptySet());

    /// <summary>Builds a delta removing an element. It tombstones only tags this state has seen, which is what makes adds win.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The delta, to be merged.</returns>
    public OrSet RemoveDelta(string element)
        => new(Maps.Empty<ImmutableSortedSet<string>>(), Maps.EmptySet().Union(Entries.TryGetValue(element, out ImmutableSortedSet<string>? tags) ? tags : Maps.EmptySet()));

    /// <summary>Merges by union of tags and of tombstones.</summary>
    /// <param name="other">The other state.</param>
    /// <returns>The merged state.</returns>
    public OrSet Merge(OrSet other)
        => new(Maps.Union(Entries, other.Entries, (a, b) => Maps.Normal(a).Union(b)), Maps.Normal(Tombstones).Union(other.Tombstones));

    /// <inheritdoc />
    public bool Equals(OrSet? other)
        => other is not null
            && Tombstones.SetEquals(other.Tombstones)
            && Maps.SameItems(Entries, other.Entries, TagSets.Instance);

    /// <inheritdoc />
    public override int GetHashCode() => Tombstones.Count ^ (Entries.Count << 8);

    private sealed class TagSets : IEqualityComparer<ImmutableSortedSet<string>>
    {
        public static readonly TagSets Instance = new();

        public bool Equals(ImmutableSortedSet<string>? x, ImmutableSortedSet<string>? y)
            => x is not null && y is not null && x.SetEquals(y);

        public int GetHashCode(ImmutableSortedSet<string> obj) => obj.Count;
    }
}
