# Roadmap

Each milestone ends with an exit check, `docs/walkthroughs/mN.md`, and a review stop. CI must be green before a milestone counts as done.

| ID | Milestone | Exit check | State |
|---|---|---|---|
| M0 | Datasync spike | Demo: edit offline, reconnect, sync; integration map complete | **Done** |
| M1 | `Leeway.Core`: counters, sets, HLC registers, escrow ledger, op log | Property tests: merge commutativity, associativity, idempotence; escrow conservation | **Done** |
| M2 | Simulator and mutation corpus, nightly CI | Simulator catches every mutant; 10,000 seeded runs green | **Done** |
| M3 | Escrow end to end | Two offline clients spend within leases, reconnect, invariant holds; overspend becomes provisional and resolves correctly | **Done** |
| M4 | Field-semantic conflict resolution | Concurrent offline edits merge per field | **Done** |
| M5 | Generator and analyzer | Snapshot tests; diagnostics and code fixes tested | **Done** |
| M6 | GateKeeper demo and launch | Published README demo and walkthrough | Exit check met. The write-up is an unpublished draft |

**Before real use.** Done: device authentication, retention of operations, discarding refused operations, entity deletion, and first-come ordering within a device (`docs/walkthroughs/hardening.md`).

MySQL server tests are also done (ADR-0019).

Still needed: upgrading to Datasync 10.2.0 when it ships; per-entity access control; compacting set tombstones; failover and throughput testing; and a real pilot.

**Later layers:** MAUI Android client, velocity-based rebalancing, more invariant types.
