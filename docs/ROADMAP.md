# Roadmap

Each milestone ends with an exit check, `docs/walkthroughs/mN.md`, and a review stop. CI must be green before a milestone counts as done.

| ID | Milestone | Exit check | State |
|---|---|---|---|
| M0 | Datasync spike | Demo: edit offline, reconnect, sync; integration map complete | **Done** |
| M1 | `Leeway.Core`: counters, sets, HLC registers, escrow ledger, op log | Property tests: merge commutativity, associativity, idempotence; escrow conservation | **Done** |
| M2 | Simulator and mutation corpus, nightly CI | Simulator catches every mutant; 10,000 seeded runs green | **Done** |
| M3 | Escrow end to end | Two offline clients spend within leases, reconnect, invariant holds; overspend becomes provisional and resolves correctly | **Done** |
| M4 | Field-semantic conflict resolution | Concurrent offline edits merge per field | **Done** |
| M5 | Generator and analyzer | Snapshot tests; diagnostics and code fixes tested | Exit check met |
| M6 | GateKeeper demo and launch | Published README demo and walkthrough | Not started |

**M6 scope added by M4:** move GateKeeper from the M0 spike onto Leeway. The sample does not use the library yet.

**Later layers:** MAUI Android client, velocity-based rebalancing, upstream contributions, more invariant types. None of these starts before M0 to M5 are done.
