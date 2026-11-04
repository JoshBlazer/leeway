# Leeway: product

**Business rules that hold, even without signal.**

Offline field apps (inspections, inventory, deliveries, event check-in, sales reps) are classic enterprise .NET.
The .NET Foundation's Datasync Community Toolkit gives .NET clients offline data, but conflict handling is whole-row: by default
nothing is resolved automatically, and the built-in resolvers are client-wins and server-wins. Nothing protects a rule like
"stock never goes negative" when devices act offline.

Leeway extends Datasync with:

- **Per-field merge semantics:** counters, add-wins sets, last-writer-wins with hybrid logical clocks.
- **Escrow-backed invariants:** each device leases a slice of a bounded quantity and can spend it offline with no risk of breaking the rule.
- **Roslyn tooling:** a source generator that turns attributes into merge code, and an analyzer that flags fields that look like they need an invariant.
- **Honest guarantee classes** for every rule.
- **A deterministic simulator** that tests invariants and convergence under partitions, clock skew, and duplicated or reordered sync.

## Guarantee classes

| Class | Meaning | Example |
|---|---|---|
| Guaranteed offline | Cannot be violated by offline activity | Zone capacity, through escrow |
| Detected on reconnect | Can happen offline, and is flagged on reconnect | One ticket scanned at two offline gates |
| Requires a connection | Cannot be decided offline | Anything needing global uniqueness at write time |

## Users

| User | Need | Surface |
|---|---|---|
| .NET app developer | Offline writes that respect business rules | NuGet packages and attributes |
| Tech lead | What exactly is guaranteed offline? | Guarantee classification and docs |
| Reviewer or evaluator | Does it really work? | Simulator results and demo app |

## Non-goals for v1

No general-purpose CRDT database, no collaborative text editing, no custom transport, and no MAUI until the Avalonia demo is done.

## Developer experience (sketch, subject to ADR-0012)

```csharp
public partial class Zone : OfflineEntity
{
    [Escrow(Min = 0, Lease = 10)]   public int RemainingCapacity { get; set; }
    [Counter]                       public int CheckIns { get; set; }
    [LastWriterWins]                public string? Notes { get; set; }
    [AddWinsSet]                    public ISet<string> Flags { get; set; } = new HashSet<string>();
}

var result = await zone.TrySpendAsync(z => z.RemainingCapacity, 1);
// Confirmed (within lease) | Provisional (beyond lease, resolved on reconnect)
```

## Demo: GateKeeper

Event check-in at venue gates with bad signal. Zone capacity is guaranteed offline through escrow. A duplicate ticket at two offline gates is detected on reconnect.
Gate notes use LWW, and flags use an add-wins set. The demo is an Avalonia desktop app plus an ASP.NET Core dashboard. Today it is only the M0 spike: one synced entity and no Leeway semantics.
