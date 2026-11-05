# Roadmap

Each milestone ends with an exit check, `docs/walkthroughs/mN.md`, and a review stop. CI must be green before a milestone counts as done.

| ID | Milestone | Exit check | State |
|---|---|---|---|
| M0 | Datasync spike | Demo: edit offline, reconnect, sync; integration map complete | **Done** |
| M1 | `Leeway.Core`: counters, sets, HLC registers, escrow ledger, op log | Property tests: merge commutativity, associativity, idempotence; escrow conservation | Exit check met |
| M2 | Simulator and mutation corpus, nightly CI | Simulator catches every mutant; 10,000 seeded runs green | Exit check met |
| M3 | Escrow end to end | Two offline clients spend within leases, reconnect, invariant holds; overspend becomes provisional and resolves correctly | Exit check met |
| M4 | Field-semantic conflict resolution | Concurrent offline edits merge per field | Exit check met |
| M5 | Generator and analyzer | Snapshot tests; diagnostics and code fixes tested | Not started |
| M6 | GateKeeper demo and launch | Published README demo and walkthrough | Not started |

M1 to M4 count as done once CI is green on them. See each walkthrough.

**M5 scope added by M4 (ADR-0015):** generate `MergeSchema` instead of using reflection, offer the sketch's friendlier API, and try `ISet<string>`.

**M6 scope added by M4:** move GateKeeper from the M0 spike onto Leeway. The sample does not use the library yet.

**Later layers:** MAUI Android client, velocity-based rebalancing, upstream contributions, more invariant types. None of these starts before M0 to M5 are done.
