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

A device clock can be wrong by a day, and an HLC does not fix that: it drags everyone forward. ADR-0004 requires a maximum drift and a rule for what happens beyond it. Three rejected options:

- **Reject the operation:** loses a real edit made in good faith.
- **Re-stamp with the server's time on arrival:** arrival order is not write order. A device's retried earlier write would overtake its later one.
- **Clamp to the limit:** the same problem, because two clamped stamps tie.

The chosen rule gives every admitted operation a **canonical stamp**:

```csharp
public sealed record StampAuthority(long MaxDriftMs, ImmutableSortedDictionary<string, long> Offsets)
{
    public (HybridTimestamp Canonical, StampAuthority Next) Admit(Operation op, long nowMs);
}
```

```
shifted = op.Stamp.Physical - offset[device]
if shifted > now + maxDrift:
    offset[device] += shifted - (now + maxDrift)     // Fix this device's offset from here on.
    shifted = now + maxDrift
physical = max(shifted, op.Observed.Physical + 1)
canonical = (physical, op.Sequence, device)
```

Why this works:

- **Causality across devices.** `Observed` is the highest canonical stamp the device had pulled when it wrote. So a write that saw a value always gets a higher stamp than that value, even if the device clock is hopelessly behind.
- **Order within a device.** One offset applies to all of a device's operations, and `Sequence` breaks ties, so a retried earlier write cannot overtake a later one.
- **Bounded influence.** A fast clock can get ahead by at most `maxDrift`, so it cannot win every merge.

## Honest limits

- **Concurrent writes.** Neither saw the other, so the order is arbitrary, though consistent. An HLC cannot know which one a person meant to win.
- **Within the drift limit, a fast clock still wins concurrent writes.** That is the price of honouring real time. The default limit is 5 minutes.
- **A rising offset can misorder one device's writes to different fields.** If a device's clock jumps forward *while* its operations are arriving out of order, a retried older write to one field can land below an even older one. It needs both faults together, and the simulator's single register cannot show it. A later write to the field fixes it.
- **A corrected clock is penalized.** The offset is kept, because the device's HLC never goes backwards either, so its writes stay in order but look older until the wall clock catches up.
