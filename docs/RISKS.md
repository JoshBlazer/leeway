# Risks

| Risk | Status | Mitigation |
|---|---|---|
| Datasync extension points may not expose what is needed | **Partly materialized in M0.** Resolver merge is unsafe, and there is no pull hook | ADR-0012 design. Upstream: #578 and #579 filed, others held (see the integration map) |
| Datasync behaviour traps: false-success timeouts, lost progress, delta gap | **Materialized in M0** (C13, C19, C24, C25). Handled in M3 | Probes pin them, the simulator models them, and end-to-end tests cover them |
| A race in Datasync's process-wide lock | **Materialized in M5,** once in CI. Fixed upstream in PR #599, unreleased | `LeewaySyncGate` serializes syncs in a process. Direct use of `OfflineDbContext` is not covered |
| Datasync version churn | **Live:** the #578 fix changes push behaviour in the next release | Pinned at 10.1.2. Probes fail if behaviour changes. Steps are in the integration map |
| Vulnerable transitive native SQLite | **Materialized in M0.** Fixed here by pins | ADR-0008, C22. **Consumers of Leeway's packages do not inherit our pins**, so they must be told |
| Licence drift in dependencies | **Materialized in M0** (Verify 33) | ADR-0009. Review licences on every upgrade |
| Test dependency incompatibility (xUnit 4 and Avalonia adapter) | Worked around | ADR-0010 |
| Stranded quota and allocation fairness | Open | ADR-0005: explicit write-off, documented risk |
| Clock skew and HLC correctness | **Materialized in M2:** two designs were wrong | ADR-0014, simulator clock-skew faults, 200,000-run soak. A rare bug could remain, which nightly runs look for |
| The simulator tests a model of the transport, not the adapters | Open | Probes, end-to-end tests, and `scripts/adapter-sabotage.py` cover the adapters, with far fewer schedules |
| Tombstone and counter-entry growth | Open | Operations can be pruned. These cannot yet, because a device can stay offline indefinitely |
| Pruning breaks exactly-once | Handled | Pruning uses what each device reports, tested end to end, in the simulator, and with a mutant |
| Local write speed | Roughly measured in M6, at about 8 to 19 ms a write | Fine for a gate on one machine. There is no benchmark suite, so a regression would go unnoticed. Connection pooling matters |
| Provisional order | Fixed within a device (ADR-0018) | Two gates still race, in the order they reconnect. One round trip per operation has not been measured over real latency |
| Rejected operations | Handled | Reported as `Failed`, they do not block pulls, and `DiscardRefusedAsync` removes them |
| Authentication misconfiguration | Handled at startup | The server will not start without a decision. There is no per-entity access control |
| Server stamp ordering outside SQLite | Open | ADR-0013 relies on a single writer. Other providers unverified |
| SQLite is not recommended for production Datasync servers | Open: **limits what Leeway can claim** | ADR-0003. State verified providers. A real deployment needs a provider Leeway has not tested |
| Offline writes blocked by an in-flight push | Solved in M3 | C23: writes go to an unqueued outbox, tested end to end |
| Avalonia and MAUI platform differences; NativeAOT on mobile | Open | CI builds the sample on three OSes and runs tests on Linux. **The desktop window has only been run for real on Windows, and macOS has no test run** |
| Package IDs are free but not reserved | Open | ADR-0007 |
| Analyzer false positives on real code | Open: GateKeeper gave none, but it is small and was written knowing the rules | Needs an outside project |
| Scope creep | Open | Production gaps in the roadmap come before any later layer |
