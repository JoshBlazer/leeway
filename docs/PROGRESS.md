# Progress

Read this first at the start of each session. Append at the end of each session.

## 2026-11-04: Session 1, scaffold and M0

**Done**

- Name checks: all `Leeway.*` NuGet IDs free; `leeway` free as a repository name under the signed-in account; `github.com/leeway` is an unrelated dormant user (ADR-0007).
- Scaffolded the solution, projects, tests, CI, and docs.
- Built the M0 spike: `GateKeeper.Server`, `GateKeeper.Desktop`.
- Wrote `docs/design/datasync-integration.md`, backed by 31 tests over real HTTP and SQLite.
- Proved the exit check locally: 42 of 42 tests in Release, from a clean clone, the live demo script passes, and the UI tests pass 192 of 192 under parallel load.

**Key findings:** see the integration map. C24 (timeout reports success), C10 (resolver write loses updates), C25 (self-conflict), C19 (delta gap), C23 (process-wide lock).

**State**

- M0 is met locally on Windows, but **not done: CI has never run.** Nothing has been pushed.
- Work is on local branch `m0-datasync-spike`. `main` holds only the initial commit.
- Git identity is set in this repository's config only, as a placeholder pending maintainer confirmation.
- The .NET SDK was installed per-user in `~/.dotnet`, not on `PATH`. See `CLAUDE.md`.

**Waiting on the maintainer**

1. Review of M0.
2. Decision on ADR-0012 and ADR-0013 (they change an emphasis in the brief).
3. GitHub owner and visibility, and approval to push so CI can run.
4. Correct git author identity.
5. Whether to file upstream proposals U1 to U7.

**Next:** after review, get CI green and then start M1 with the HLC and merge design notes. Do not start M1 before review.

**Simulation metrics:** runs 0; mutant catch rate not measured; shrunk regressions 0.

## 2026-11-04: Session 1, continued, after maintainer review

**Decisions from the maintainer**

- ADR-0012 and ADR-0013 accepted.
- Repository is public at `JoshBlazer/leeway`.
- All commits are authored only as `Joshua Willie <joshblazerwillie@gmail.com>`, with no co-author or tool trailers unless explicitly asked. Local history was rewritten to match before the first push. See `CLAUDE.md`.
- Upstream filing left to engineering judgement.

**Done**

- Pushed, and opened PR #1. [CI is green](https://github.com/JoshBlazer/leeway/actions/runs/42021427010) on its first run: 42 of 42 tests on Linux, and the sample builds on Linux, macOS, and Windows.
- Filed upstream [#578](https://github.com/CommunityToolkit/Datasync/issues/578) (timeout reports success) and [#579](https://github.com/CommunityToolkit/Datasync/issues/579) (resolver write loses updates). U3 to U7 are held.
- Corrected the C19 scope: it is shown on millisecond-precision SQLite, and source suggests finer stores are not affected.

**State:** the M0 exit check is met and CI is green. PR #1 was later merged by fast-forward on the maintainer's word.

**Simulation metrics:** runs 0; mutant catch rate not measured; shrunk regressions 0.

## 2026-11-05: Session 2, M1 to M4

The maintainer asked for M1 to M4 in one run, so there were no review stops between them.

**Done**

- Design notes first: `hlc.md`, `merge-semantics.md`, `escrow-protocol.md`, `simulation.md`.
- M1 `Leeway.Core`, M2 simulator and 18 mutants, M3 escrow end to end, M4 per-field merging.
- `scripts/adapter-sabotage.py`, and a nightly soak workflow, which has not run yet because it needs to be on `main`.
- ADR-0014 (stamp admission) and ADR-0015 (reflection and JSON sets, until the generator).

**Key findings**

- The simulator found 3 real bugs in the correct code, after all property tests were green. See `docs/walkthroughs/m2.md`.
- Sabotage found 3 adapter protections no test really covered. See `docs/walkthroughs/m3.md`.

**State:** merged to `main` by fast-forward on the maintainer's word (PR #2).

**Known gaps to pick up:** retention of op tables and outboxes, a discard path for refused operations, entity deletion, device authentication hook, local write benchmark. GateKeeper still needs moving onto Leeway (M6). 
**Upstream:** the maintainer fixed #578 in PR #581 the same day, not yet released, and is looking at #579. The fix makes a timed-out push throw `TimeoutException`, which `LeewayClient` now handles. Before upgrading past 10.1.2, read "When upgrading" in the integration map, because probes C13, C24, and C25 will need updating.


**Simulation metrics:** longest run 200,000, no failures; mutant catch rate 18 of 18; shrunk regressions 18.

## 2026-11-05: Session 2, continued, M5

**Done**

- Merged PR #2. Triggered the nightly soak by hand: 200,000 runs from start seed 572291612, no failures, and 18 of 18 mutants caught.
- M5: `docs/design/generator.md`, generated schema and typed API, rules LEE001 to LEE006, code fixes, and `scripts/package-check.py` in CI. ADR-0016 added, and ADR-0015 updated.
- Tried `ISet<string>`: EF Core refuses it without a converter, so sets stay as JSON strings.

**State:** merged to `main` by fast-forward on the maintainer's word (PR #3).

**Upstream:** #579 was fixed in PR #582, unreleased. CI found a race in Datasync's lock, reported as #596, with `LeewaySyncGate` as the defence.

**Still open from earlier:** retention, a discard path for refused operations, entity deletion, a device authentication hook, a local write benchmark.

**Simulation metrics:** longest local run 200,000, no failures; mutant catch rate 18 of 18; shrunk regressions 18.

## 2026-11-05: Session 2, continued, M6

**Done**

- Merged PR #3. M6: GateKeeper rebuilt on Leeway (server, dashboard, desktop), `scripts/m6-demo.ps1`, `scripts/make-demo-gif.py`, the README demo, and a draft write-up in `docs/writeup.md`.

**Key findings:** provisional spends are not first come, first served. Local writes take about 8 to 19 ms with pooling on, roughly measured. Looking at the GIF found three problems no assertion caught. See `docs/walkthroughs/m6.md`.

**Upstream:** #578, #579, and #596 are all fixed by the maintainer (PRs #581, #582, #599). None is released yet, and we stay pinned at 10.1.2. The maintainer has said they will try to release 10.2.0 soon.

**State:** all six milestones are met and merged (PR #4).

**Simulation metrics:** longest run 200,000 locally, and 200,000 in nightly CI from a random seed, no failures; mutant catch rate 18 of 18; shrunk regressions 18.

## 2026-11-05: Session 2, continued, hardening

The maintainer said to do all of: merge M6, approve the write-up, and carry on with the next work.

**Done**

- Merged PR #4, and removed the write-up's draft banner. It lives in this repository only. Nothing was posted elsewhere.
- Device authentication, retention, discarding refused operations, entity deletion, and first-come ordering within a device. See `docs/walkthroughs/hardening.md`, ADR-0017, and ADR-0018.

**Not done, and why**

- **Datasync 10.2.0:** not released as of 2026-11-05. Upgrade when it ships, following "When upgrading" in the integration map.
- **Other server databases:** the maintainer then allowed MySQL. See the next entry.

**State:** merged (PR #5).

**Simulation metrics:** longest run 200,000 locally, and 200,000 in nightly CI from a random seed, no failures; mutant catch rate 19 of 19; shrunk regressions 19.

## 2026-11-05: Session 2, continued, MySQL

**Done**

- The maintainer relaxed constraint 4 for server tests, suggesting MySQL (ADR-0019). Every end-to-end test can now run its server on MySQL, and CI does so. Multi-server tests were added.
- Two bugs SQLite could not show were found and fixed: a lock-upgrade deadlock, and a stale first write.
- The maintainer left posting the write-up to my judgement. It stays in the repository, linked from the README, because posting elsewhere under their name is their call. Repository topics were added.

**State:** merged (PR #6).

**Simulation metrics:** unchanged. Mutant catch rate 19 of 19.

## 2026-11-06: Session 2, continued, Datasync 10.2.0

**Done**

- Datasync 10.2.0 was released today, and Leeway now uses it. Only probes C13, C24, and C25 failed, as predicted, and they now pin the new behaviour.
- The SQLite security pins are gone, because upstream fixed the advisory. The package check now confirms what a consumer resolves: native SQLite 3.53.4.
- The sabotage case for the queue check stopped being caught, because upstream fixed the path it guards. The check stays as a guard, and the case is gone (19 cases, plus 2 for MySQL).
- CI now breaks the MySQL lock and collation check on each run. Both were caught, which closes that gap.

**Incident:** the system stopped the full sabotage run for low memory (about 1.7 GB free of 16 GB), and that left a sabotage in `LeewayAuthority.cs`. It was never committed, and was restored with `git restore`. The script now refuses a changed tree. The local Debug binaries may still contain it until the next build.

**Not proven:** the full sabotage run on 10.2.0. Only the first of 19 cases finished, and it was caught.

**State:** on branch `datasync-10.2.0`, in PR #7, awaiting review. Not merged.

**Next:** merge on the maintainer's word. Then per-entity access control, tombstone compaction, failover and throughput testing, and a real pilot.

**Simulation metrics:** not re-run for this upgrade, because the simulator runs on `Leeway.Core` with a modelled transport and uses no Datasync code.

