using System.Collections.Immutable;
using System.Text.Json;

namespace Leeway.Core;

/// <summary>The merge state of one entity, field by field. A field operation is a small document merged into a larger one.</summary>
/// <param name="Counters">Counter fields.</param>
/// <param name="Registers">Last-writer-wins fields.</param>
/// <param name="Sets">Add-wins set fields.</param>
public sealed record MergeDocument(
    ImmutableSortedDictionary<string, PnCounter> Counters,
    ImmutableSortedDictionary<string, LwwRegister> Registers,
    ImmutableSortedDictionary<string, OrSet> Sets)
{
    /// <summary>A document with no fields.</summary>
    public static MergeDocument Empty { get; } = new(Maps.Empty<PnCounter>(), Maps.Empty<LwwRegister>(), Maps.Empty<OrSet>());

    /// <summary>Reads a counter field.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>Its state, or an empty counter.</returns>
    public PnCounter Counter(string field) => Counters.GetValueOrDefault(field) ?? PnCounter.Empty;

    /// <summary>Reads a set field.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>Its state, or an empty set.</returns>
    public OrSet Set(string field) => Sets.GetValueOrDefault(field) ?? OrSet.Empty;

    /// <summary>Replaces a counter field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="state">The new state.</param>
    /// <returns>The new document.</returns>
    public MergeDocument WithCounter(string field, PnCounter state) => this with { Counters = Maps.Normal(Counters).SetItem(field, state) };

    /// <summary>Replaces a register field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="state">The new state.</param>
    /// <returns>The new document.</returns>
    public MergeDocument WithRegister(string field, LwwRegister state) => this with { Registers = Maps.Normal(Registers).SetItem(field, state) };

    /// <summary>Replaces a set field.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="state">The new state.</param>
    /// <returns>The new document.</returns>
    public MergeDocument WithSet(string field, OrSet state) => this with { Sets = Maps.Normal(Sets).SetItem(field, state) };

    /// <summary>Merges field by field. Commutative, associative, and idempotent.</summary>
    /// <param name="other">The other document.</param>
    /// <returns>The merged document.</returns>
    public MergeDocument Merge(MergeDocument other) => new(
        Maps.Union(Counters, other.Counters, (a, b) => a.Merge(b)),
        Maps.Union(Registers, other.Registers, (a, b) => a.Merge(b)),
        Maps.Union(Sets, other.Sets, (a, b) => a.Merge(b)));

    /// <summary>Gives every register in a delta the canonical stamp from the server's admission.</summary>
    /// <param name="canonical">The canonical stamp.</param>
    /// <returns>The restamped document.</returns>
    public MergeDocument WithRegisterStamps(HybridTimestamp canonical)
        => this with { Registers = Maps.Normal(Registers).ToImmutableSortedDictionary(x => x.Key, x => x.Value with { Stamp = canonical }, StringComparer.Ordinal) };

    /// <summary>The highest stamp of any register. A device must keep its clock past this.</summary>
    /// <returns>The highest stamp, or the default stamp if there are no registers.</returns>
    public HybridTimestamp HighestStamp()
    {
        HybridTimestamp highest = default;
        foreach (LwwRegister register in Registers.Values)
        {
            if (register.Stamp.CompareTo(highest) > 0)
            {
                highest = register.Stamp;
            }
        }

        return highest;
    }

    /// <summary>Serializes the document.</summary>
    /// <returns>JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Deserializes a document.</summary>
    /// <param name="json">JSON text, or nothing for an empty document.</param>
    /// <returns>The document.</returns>
    public static MergeDocument FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Empty;
        }

        MergeDocument raw = JsonSerializer.Deserialize<MergeDocument>(json) ?? Empty;
        return new MergeDocument(
            Maps.Normal(raw.Counters).ToImmutableSortedDictionary(x => x.Key, x => new PnCounter(Maps.Normal(x.Value.Increments), Maps.Normal(x.Value.Decrements)), StringComparer.Ordinal),
            Maps.Normal(raw.Registers),
            Maps.Normal(raw.Sets).ToImmutableSortedDictionary(
                x => x.Key,
                x => new OrSet(
                    Maps.Normal(x.Value.Entries).ToImmutableSortedDictionary(e => e.Key, e => Maps.Normal(e.Value), StringComparer.Ordinal),
                    Maps.Normal(x.Value.Tombstones)),
                StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public bool Equals(MergeDocument? other)
        => other is not null
            && Maps.SameItems(Counters, other.Counters, EqualityComparer<PnCounter>.Default)
            && Maps.SameItems(Registers, other.Registers, EqualityComparer<LwwRegister>.Default)
            && Maps.SameItems(Sets, other.Sets, EqualityComparer<OrSet>.Default);

    /// <inheritdoc />
    public override int GetHashCode() => Counters.Count ^ (Registers.Count << 8) ^ (Sets.Count << 16);
}
