# Risks

| Risk | Status | Mitigation |
|---|---|---|
| Datasync extension points may not expose what is needed | **Partly materialized in M0.** Resolver merge is unsafe, and there is no pull hook | ADR-0012 design. Upstream proposals U1 to U7 (none filed yet) |
| Datasync behaviour traps: false-success timeouts, lost progress, delta gap | **Materialized in M0** (C13, C19, C24, C25) | Probes pin them; simulator must model them |
| Datasync version churn | Open | Pinned at 10.1.2. Probes fail if behaviour changes |
| Vulnerable transitive native SQLite | **Materialized in M0.** Fixed here by pins | ADR-0008, C22. **Consumers of Leeway's packages do not inherit our pins**, so they must be told |
| Licence drift in dependencies | **Materialized in M0** (Verify 33) | ADR-0009. Review licences on every upgrade |
| Test dependency incompatibility (xUnit 4 and Avalonia adapter) | Worked around | ADR-0010 |
| Stranded quota and allocation fairness | Open | ADR-0005: explicit write-off, documented risk |
| Clock skew and HLC correctness | Open | ADR-0004, design note before M1 code, simulator clock-skew faults |
| A far-future device clock dragging HLCs forward | Open | Design note must define a maximum drift |
| Server stamp ordering outside SQLite | Open | ADR-0013 relies on a single writer. Other providers unverified |
| SQLite is not recommended for production Datasync servers | Open: **limits what Leeway can claim** | ADR-0003. State verified providers. A real deployment needs a provider Leeway has not tested |
| Offline writes blocked by an in-flight push | Open | C23: unqueued local writes. Outbox design is an M3 open question |
| Op table growth | Open | Retention policy design in M3 |
| Avalonia and MAUI platform differences; NativeAOT on mobile | Open | CI builds on three OSes. **macOS and Linux have not yet run** |
| Package IDs are free but not reserved | Open | ADR-0007 |
| Scope creep | Open | M0 to M5 before any later layer |
