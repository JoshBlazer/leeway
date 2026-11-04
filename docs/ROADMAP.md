# Roadmap

Each milestone ends with an exit check, `docs/walkthroughs/mN.md`, and a review stop. CI must be green before a milestone counts as done.

| ID | Milestone | Exit check | State |
|---|---|---|---|
| M0 | Datasync spike | Demo: edit offline, reconnect, sync; integration map complete | Exit check met, CI green. Tests on Linux and Windows; sample built on Linux, macOS, and Windows |
| M1 | `Leeway.Core`: counters, sets, HLC registers, escrow ledger, op log | Property tests: merge commutativity, associativity, idempotence; escrow conservation | Not started |
| M2 | Simulator and mutation corpus, nightly CI | Simulator catches every mutant; 10,000 seeded runs green | Not started |
| M3 | Escrow end to end | Two offline clients spend within leases, reconnect, invariant holds; overspend becomes provisional and resolves correctly | Not started |
| M4 | Field-semantic conflict resolution | Concurrent offline edits merge per field | Not started |
| M5 | Generator and analyzer | Snapshot tests; diagnostics and code fixes tested | Not started |
| M6 | GateKeeper demo and launch | Published README demo and walkthrough | Not started |

**M2 scope added by M0:** the simulator's fault model must include false-success timeouts (C24), mid-batch loss of progress (C13), lost responses and timeouts after apply (C12, C25), and same-millisecond stamps (C19). Mutants should include a client that trusts `PushResult.IsSuccessful` and a merge that uses the unconditional resolver write (C10).

**Later layers:** MAUI Android client, velocity-based rebalancing, upstream contributions, more invariant types. None of these starts before M0 to M5 are done.
