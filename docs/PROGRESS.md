# Progress

## Current state

- Milestones M0 to M6 are complete, along with the hardening list, MySQL server tests, and the upgrade to Datasync 10.2.0.
- **Next:** per-entity access control, tombstone compaction, failover and throughput testing, and a real pilot.
- **Simulation:** longest run 200,000, locally and in nightly CI from a random seed, with no failures. Mutant catch rate 19 of 19. Shrunk regressions 19.
- **Adapter sabotage:** 19 of 19 caught on Datasync 10.2.0, plus both MySQL cases.

## History

### 2026-11-06: Datasync 10.2.0

- Datasync 10.2.0 was released, and Leeway now uses it. Only probes C13, C24, and C25 failed, as predicted, and they now pin the new behaviour.
- The SQLite security pins are gone, because upstream fixed the advisory. The package check now confirms what a consumer resolves: native SQLite 3.53.4.
- The sabotage case for the queue check stopped being caught, because upstream fixed the path it guards. The check stays as a guard, and the case is gone (19 cases, plus 2 for MySQL).
- CI now breaks the MySQL lock and collation check on each run, and both were caught.
- A full sabotage run was stopped for low memory, and left a sabotage in the working tree of `LeewayAuthority.cs`. It was never committed. The script now refuses a changed tree, and the full run moved to a manual workflow, `adapter-sabotage.yml`, which caught 19 of 19 in about six minutes (run 42274587119).
- The simulator was not re-run for this upgrade, because it runs on `Leeway.Core` with a modelled transport and uses no Datasync code.

### 2026-11-05: MySQL (PR #6)

- Every end-to-end test can now run its server on MySQL, and CI does so. Multi-server tests were added (ADR-0019).
- Two bugs SQLite could not show were found and fixed: a lock-upgrade deadlock, and a stale first write.
- Repository topics were added. The write-up stays in the repository, linked from the README.

### 2026-11-05: Hardening (PR #5)

- Device authentication, retention, discarding refused operations, entity deletion, and first-come ordering within a device. See `docs/walkthroughs/hardening.md`, ADR-0017, and ADR-0018.
- The write-up's draft banner was removed.
- Mutant catch rate 19 of 19, and shrunk regressions 19.

### 2026-11-05: M6 (PR #4)

- GateKeeper rebuilt on Leeway (server, dashboard, desktop), `scripts/m6-demo.ps1`, `scripts/make-demo-gif.py`, the README demo, and a write-up in `docs/writeup.md`.
- **Findings:** provisional spends are not first come, first served. Local writes take about 8 to 19 ms with pooling on, roughly measured. Looking at the GIF found three problems no assertion caught. See `docs/walkthroughs/m6.md`.
- **Upstream:** Datasync's maintainer fixed #578, #579, and #596 (PRs #581, #582, #599).

### 2026-11-05: M5 (PR #3)

- `docs/design/generator.md`, a generated schema and typed API, rules LEE001 to LEE006, code fixes, and `scripts/package-check.py` in CI. ADR-0016 added, and ADR-0015 updated.
- The nightly soak's first run: 200,000 runs from start seed 572291612, no failures, and 18 of 18 mutants caught.
- `ISet<string>` was tried. EF Core refuses it without a converter, so sets stay as JSON strings.
- **Upstream:** CI found a race in Datasync's lock, reported as #596, with `LeewaySyncGate` as the defence.

### 2026-11-05: M1 to M4 (PR #2)

- Design notes first: `hlc.md`, `merge-semantics.md`, `escrow-protocol.md`, `simulation.md`.
- M1 `Leeway.Core`, M2 simulator and 18 mutants, M3 escrow end to end, M4 per-field merging.
- `scripts/adapter-sabotage.py`, and a nightly soak workflow.
- ADR-0014 (stamp admission) and ADR-0015 (reflection and JSON sets, until the generator).
- **Findings:** the simulator found 3 real bugs in the correct code, after all property tests were green (`docs/walkthroughs/m2.md`). Sabotage found 3 adapter protections no test really covered (`docs/walkthroughs/m3.md`).

### 2026-11-04: Scaffold and M0 (PR #1)

- Name checks: all `Leeway.*` NuGet IDs were free (ADR-0007).
- The solution, projects, tests, CI, and docs were scaffolded, and the M0 spike built: `GateKeeper.Server`, `GateKeeper.Desktop`.
- `docs/design/datasync-integration.md`, backed by 31 tests over real HTTP and SQLite. Key findings: C24 (timeout reports success), C10 (resolver write loses updates), C25 (self-conflict), C19 (delta gap), C23 (process-wide lock).
- ADR-0012 and ADR-0013 accepted.
- [CI was green](https://github.com/JoshBlazer/leeway/actions/runs/42021427010) on its first run: 42 of 42 tests on Linux, and the sample builds on Linux, macOS, and Windows.
- **Upstream:** filed [#578](https://github.com/CommunityToolkit/Datasync/issues/578) (timeout reports success) and [#579](https://github.com/CommunityToolkit/Datasync/issues/579) (resolver write loses updates). U3 to U7 are held.
- The C19 scope was corrected: it is shown on millisecond-precision SQLite, and source suggests finer stores are not affected.
