# Design note: merge semantics

Code: `src/Leeway.Core/Crdts.cs`, `MergeDocument.cs`. Tests: `tests/Leeway.Property.Tests/MergeLawProperties.cs`.

## Principle

ADR-0012 carries intent in operations. M0 showed delivery is at-least-once and unordered (C12, C13, C25, no `ORDER BY`). So **each field operation is a small piece of CRDT state (a delta), and applying one is just a merge.** Merge is commutative, associative, and idempotent, so duplicates and reordering are harmless by construction. The op table's unique ID then gives a second line of defence and carries the outcome.

The same function runs in three places: server (authoritative), client (local view), and simulator.

## Types

```csharp
public sealed record PnCounter(Increments, Decrements)      // Per-replica cumulative totals.
{
    public long Value { get; }
    public PnCounter AddDelta(string replica, long amount); // Only this replica's new cumulative total.
    public PnCounter Merge(PnCounter other);                // Per-replica maximum.
}

public sealed record LwwRegister(string? Value, HybridTimestamp Stamp)
{
    public LwwRegister Merge(LwwRegister other);            // Higher stamp wins.
}

public sealed record OrSet(Entries, Tombstones)             // Element to unique tags, plus removed tags.
{
    public IEnumerable<string> Elements { get; }
    public OrSet AddDelta(string element, string tag);
    public OrSet RemoveDelta(string element);               // Tombstones only the tags this replica has seen.
    public OrSet Merge(OrSet other);                        // Union, minus tombstones.
}

public sealed record MergeDocument(Counters, Registers, Sets)
{
    public MergeDocument Merge(MergeDocument other);        // Field by field.
}
```

## Why these give the right behaviour

- **Counters** store each replica's own cumulative total. A replay carries the same total, so `max` absorbs it. Two devices each adding 5 give 10.
- **Sets are add-wins.** A remove can only tombstone tags it has seen, and a concurrent add has a fresh tag, so it survives.
- **Registers** compare canonical stamps (see `hlc.md`). The equal-stamp tie-break on value exists only to keep the laws total.

## The local view includes settling deltas

The first version of the simulator found a protocol bug here. After an operation is acknowledged, it leaves the pending list, but the device may not have pulled the row that includes it. A view built from `pulled + pending` then lacks the device's own write, so the next counter add computes its cumulative total from a stale base. The lower total is absorbed by `max`, and an increment is silently lost.

The view is therefore `pulled + settling + pending`, where a settling delta is dropped once a pulled row's stamp reaches the stamp returned with its acknowledgement. Merging an acknowledged delta straight into the pulled state would not work, because its register stamps are the device's rather than the canonical ones.

## Exit check

Property tests of commutativity, associativity, and idempotence for all four types.

## Honest limits

- **Tombstones and per-replica entries grow forever.** Compaction requires knowing every replica has seen a tombstone, and a device may stay offline indefinitely. This is not solved in v1.
- **A counter has no bound.** Use escrow if it must not cross a limit.
- **Add-wins is a policy.** A remove racing an add loses, which is wrong for some domains, such as revoking access.
- **Registers lose one write.** Concurrent writers to a field do not merge their text. One value wins, and that is not reported in v1.
- **Deleting entities is not implemented.** Merging a delete against concurrent edits needs its own policy.
