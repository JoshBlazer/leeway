# Design note: hybrid logical clocks and stamp admission

Implements ADR-0004. Code: `src/Leeway.Core/Clocks.cs`. Tests: `tests/Leeway.Property.Tests/ClockProperties.cs`.

## Problem

Last-writer-wins needs an order. Wall clocks give one, but a device whose clock is ahead wins every merge, and a device whose clock is behind loses to writes it has already seen. An edit made *after* seeing a value must beat that value, whatever the clocks say.

## Device side: the clock

```csharp
public readonly record struct HybridTimestamp(long Physical, int Logical, string Replica) : IComparable<HybridTimestamp>;

public sealed class HybridClock
{
    public HybridClock(string replica, Func<long> wallClock, HybridTimestamp? last = null);
    public HybridTimestamp Last { get; }
    public HybridTimestamp Now();                        // Strictly greater than Last, and than anything observed.
    public HybridTimestamp Observe(HybridTimestamp seen);
}
```

The order is `Physical`, then `Logical`, then `Replica` (ordinal), which is total, so any two stamps compare the same way on every replica.

## Server side: stamp admission

A device clock can be wrong by a day, and an HLC does not fix that: it drags everyone forward. ADR-0004 requires a maximum drift and a rule for what happens beyond it. Rejected options:

- **Reject the operation:** loses a real edit made in good faith.
- **Re-stamp with the server's time on arrival:** arrival order is not write order. A device's retried earlier write would overtake its later one.
- **Keep a per-device offset.** This was the first design. The simulator disproved it: shuffled delivery could raise the offset between two writes, so a later write landed below an earlier one (9 runs in 10,000).
- **Keep one watermark per device.** This was the second design, and the simulator disproved it too: a late arrival clamped against a raised watermark could land above a write admitted before the raise (9 runs in 50,000).

The chosen rule gives every admitted operation a **canonical stamp**:

```csharp
public sealed record StampAuthority(long MaxDriftMs, Admitted /* device -> sequence -> physical */)
{
    public (HybridTimestamp Canonical, StampAuthority Next) Admit(Operation op, long nowMs);
}
```

```
physical = min(op.Stamp.Physical, now + maxDrift)
physical = max(physical, highest physical admitted at a lower sequence)     // Floor.
physical = min(physical, lowest physical admitted at a higher sequence)     // Ceiling.
physical = max(physical, op.Observed.Physical + 1)
canonical = (physical, op.Sequence, device)
```

Why this works:

- **Causality across devices.** `Observed` is the highest canonical stamp the device had pulled when it wrote. So a write that saw a value always gets a higher stamp than that value, even if the device clock is hopelessly behind.
- **Order within a device.** Each operation lands between its neighbours in sequence order, whatever the arrival order, and `Sequence` breaks ties among clamped stamps. A later operation's `Observed` is at least as high as an earlier one's, so the last step cannot cross the ceiling. So a retried earlier write cannot overtake a later one.
- **Bounded influence.** A fast clock can get ahead by at most `maxDrift`, so it cannot win every merge.
- **No lasting penalty.** Nothing is remembered about a device's clock error, so correcting the clock needs no recovery.
- **Bounded memory.** Each operation carries `SettledBelow`, the device's oldest unacknowledged sequence. Nothing lower can arrive again, so those entries are dropped, apart from one kept as a floor.

## Honest limits

- **Concurrent writes.** Neither saw the other, so the order is arbitrary, though consistent. An HLC cannot know which one a person meant to win.
- **Within the drift limit, a fast clock still wins concurrent writes.** That is the price of honouring real time. The default limit is 5 minutes.
- **A device's HLC never goes backwards.** After a clock is corrected from the future, the device's own stamps stay high until real time catches up. The server clamps them, so this costs nothing beyond the drift limit.
- **A device that is offline with many unacknowledged operations keeps its entries** until it reconnects. One entry per known device is kept forever.
