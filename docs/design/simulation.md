# Design note: simulator

Code: `src/Leeway.Simulation`, `mutants/Leeway.Mutants`. Tests: `tests/Leeway.Simulation.Tests`.

## Goal

Test the protocol under faults, deterministically, and measure how good that test is with deliberately broken implementations.

## What it runs

The sync lock in Datasync is process-wide (C23), so N real clients cannot run concurrently in one process. The simulator therefore runs the **real pure logic from `Leeway.Core`** (merges, ledger, wallet, stamp admission) with a **modelled transport**. Datasync's real behaviour is covered separately by the probes and integration tests.

That is a limit: the simulator proves the protocol, not the adapters. A bug in `Leeway.Datasync.Server` will not show up here.

## Determinism

No threads, no wall clock, and no ambient randomness. A run is a pure function of its schedule:

```csharp
public sealed record SimEvent(EventType Type, int Device = 0, long Value = 0, Fault Fault = Fault.None);
public sealed record Schedule(int Devices, long Total, ImmutableArray<SimEvent> Events);

public static class ScheduleGenerator { public static Schedule Generate(int seed); }
public static class Simulator { public static SimResult Run(Schedule schedule, ProtocolParts? parts = null); }
public static class Shrinker { public static Schedule Shrink(Schedule failing, Func<Schedule, bool> stillFails); }
```

A test asserts that the same seed gives the same trace hash.

## Faults

| Fault | Models | Evidence |
|---|---|---|
| `Partition`, `Heal` | No signal | C02 |
| `RequestLost` | The request never arrives | C02 |
| `ResponseLost` | The server applied it, and the device never heard | C12 |
| `Duplicate` | The same operation delivered twice | |
| `Timeout` | A push times out with nothing sent. Up to Datasync 10.1.2 it also reported success | C24 |
| `TimeoutAfterApply` | The same, but the server did apply it | C25 |
| `MidBatchDrop` | Earlier successes in the batch are forgotten | C13 |
| Reorder | Each push shuffles pending operations | No `ORDER BY` upstream |
| `Skew` | A device clock moves, up to days | ADR-0004 |
| `Crash` | Volatile state is lost, durable state is kept | |
| Same-millisecond stamps | Virtual time only moves on `Advance` | C19 |

## Properties

After every event:

1. **Confirmed state never violates an invariant:** `Consumed <= Total`, `Unleased >= 0`, and every `Held >= 0`. With no write-off, there is no `Overdraft`, and total device spends are at most `Total`.
4. **Conservation:** `Total == Unleased + sum(Held) + Consumed`.

After healing all partitions and syncing until quiet:

2. **Convergence:** every device's view equals the server's document, and every grant matches.
3. **No double application:** no operation ID was applied more than once.
5. **No lost updates:** the counter equals the sum of everything issued, and the set equals the adds not removed.
6. **Causality:** the register's final value was not superseded by a write that saw it.
7. **Accounts agree:** each device's reported spends equal the server's `Spent + Overdraft`.

Property 8, **bounded clock influence**, is checked after every event: no register's stamp is further ahead than the drift limit allows.

## What it found

On its first runs, before any mutant was considered, the simulator failed the correct implementation. That made the first "100% catch rate" meaningless, so a mutant now only counts as caught on a schedule the correct code passes.

| Bug | Seen in | Fix |
|---|---|---|
| An acknowledged add missing from the view lost the next increment | About a third of runs | Settling deltas (`merge-semantics.md`) |
| A raised per-device offset let a later write land below an earlier one | 9 in 10,000 runs | Dropped offsets (`hlc.md`) |
| A raised watermark let a late arrival land above an earlier write | 9 in 50,000 runs | Place each write between its neighbours (`hlc.md`) |

All three passed every unit and property test that existed at the time. The two rare ones show why the soak runs far past the exit check's 10,000, and why it matters that a soak can still find something.

## Shrinking

`Shrinker` uses delta debugging: remove chunks of events, keep any removal that still fails, and shrink the chunk size down to single events. The result is 1-minimal, meaning that removing any single event makes the failure go away. Shrunk schedules are saved as JSON under `tests/Leeway.Simulation.Tests/regressions/`, and tests replay them against both the mutant and the correct code.

## Mutation corpus

`ProtocolParts` holds the replaceable decisions as delegates, with the real functions as defaults. A mutant is a named change to those parts. Catch rate is caught mutants divided by all mutants, searching up to a fixed number of seeds. The mutants must be plausible mistakes, and the corpus must include those the brief names plus each M0 trap.

**A 100% catch rate only means the simulator catches these mutants.** It says nothing about the mistakes nobody thought of. It also says nothing about the adapters, which `scripts/adapter-sabotage.py` tests instead.

## Commands

```
dotnet test --project tests/Leeway.Simulation.Tests -c Release
dotnet run --project mutants/Leeway.Mutants -c Release -- catch
dotnet run --project mutants/Leeway.Mutants -c Release -- soak 10000 1
```

Nightly CI runs `soak` from a random start seed and uploads any failing schedule as an artifact.
