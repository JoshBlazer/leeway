# Progress

Read this first at the start of each session. Append at the end of each session.

## 2026-11-04: Session 1, scaffold and M0

**Done**

- Name checks: all `Leeway.*` NuGet IDs free; `leeway` free as a repository name under the signed-in account; `github.com/leeway` is an unrelated dormant user (ADR-0007).
- Scaffolded the solution, projects, tests, CI, and docs.
- Built the M0 spike: `GateKeeper.Server`, `GateKeeper.Desktop`.
- Wrote `docs/design/datasync-integration.md`, backed by 31 tests over real HTTP and SQLite.
- Proved the exit check locally: 41 of 41 tests in Release, the live demo script passes, and the UI tests pass 192 of 192 under parallel load.

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
