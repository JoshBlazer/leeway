# Design note: GateKeeper demo

Event check-in at venue gates with bad signal. It moves the M0 spike onto Leeway, using only the public API, and it is the first real use of the generator and analyzer. Code: `samples/GateKeeper.*`. Tests: `tests/GateKeeper.Desktop.Tests`.

## Rules and their guarantee classes

| Rule | Class | How |
|---|---|---|
| A zone never overfills | **Guaranteed offline** | `RemainingCapacity` is `[Escrow]`. A gate admits only what it holds in its lease |
| Admitting beyond the lease | **Requires a connection** | The spend is provisional, and the server decides on reconnect. A rejection means turning the person away |
| A ticket is used once | **Detected on reconnect** | Two offline gates cannot know about each other. `ScannedAt` is an `[AddWinsSet]`, so both scans survive, and more than one gate in it flags a duplicate |
| The same gate scans a ticket twice | Guaranteed offline, at that gate | Its local view includes its own scans |
| A ticket is valid for its zone | Guaranteed offline, for tickets seeded before going offline | Checked against the local copy |
| Gate notes | Last writer wins | `[LastWriterWins]`. One concurrent note is lost, and that is not reported |
| Zone flags | Add wins | `[AddWinsSet]` |
| Check-in count | Merged | `[Counter]` |

## A duplicate costs capacity

A ticket scanned at two offline gates spends capacity twice, because both gates admitted someone. That is the safe direction: the venue can end up under capacity, never over. The dashboard lists the duplicates, so staff can follow up.

## Scan order

```
Unknown ticket, or wrong zone       -> Refused
Already scanned, as far as we know  -> Refused
Lease covers it                     -> Admit                        (spend, add scan, count)
Lease does not cover it             -> Provisional                  (spend, add scan, count)
```

After a sync, a rejected provisional scan removes its scan and takes back its count, because the person was turned away.

**Provisional scans are first come, first served within a gate** (ADR-0018). Two gates still race, in the order they reconnect.

**These writes are separate transactions.** A crash between them could leave a spend with no scan. Leeway has no multi-entity operation, so this is a real limit. It errs on the safe side, since capacity is spent, not created.

## Dashboard

`GateKeeper.Server` exposes `/admin/state`, read-only and unauthenticated, as a sample only. `GateKeeper.Dashboard` is a separate small app whose page polls through it, with no external scripts or fonts. Each zone is a stacked bar: consumed, held by gates, and unleased. Those three always sum to the capacity, which is the conservation property, made visible.

## Demo evidence

- `tests/GateKeeper.Desktop.Tests`: real windows through Avalonia's headless platform, with real input, against the real server on a real socket. Stopping it refuses connections for real. CI runs this.
- `scripts/m6-demo.ps1`: separate processes and real windows, driven by UI Automation. Windows only.
- `docs/assets/gatekeeper-demo.gif`: frames captured from the first of those, plus the dashboard in a headless browser. **It is a sequence of captured frames, not a screen recording.**
