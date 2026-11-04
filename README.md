# Leeway

**Business rules that hold, even without signal.**

Leeway will extend the .NET Foundation's [Datasync Community Toolkit](https://github.com/CommunityToolkit/Datasync) with per-field merge semantics,
escrow-backed invariants ("stock never goes negative", even with every device offline), Roslyn tooling, and a deterministic simulator
that tests all of it under partitions, clock skew, and duplicated or reordered sync.

> **Status: early. Nothing described as "will" exists yet, and there are no packages to install.**
> The Datasync spike (M0) is finished and awaiting review. See [the product brief](docs/PRODUCT.md) for the plan.

## Status

"Done" means a command proves it. Requires the .NET 10 SDK.

| Milestone | State | Proving command | Last local result |
|---|---|---|---|
| Scaffold: builds with warnings as errors in `src/` | Proven locally | `dotnet build Leeway.slnx -c Release` | 0 errors |
| All tests | Proven locally | `dotnet test --solution Leeway.slnx` | 42 of 42 pass |
| **M0** Edit offline, reconnect, sync (headless UI, real server) | Proven locally | `dotnet test --project tests/GateKeeper.Desktop.Tests` | 4 of 4 pass |
| **M0** Same demo with real processes (Windows) | Proven locally | `powershell -ExecutionPolicy Bypass -File scripts/m0-demo.ps1` | `M0 DEMO PASSED` |
| **M0** Datasync behaviour map | Proven locally | `dotnet test --project tests/Leeway.Integration.Tests` | 31 of 31 pass |
| **CI** on Linux, macOS, Windows | **Not yet run** | `.github/workflows/ci.yml`, once pushed | No run yet |
| M1 to M6 | Not started | See [roadmap](docs/ROADMAP.md) | |

A milestone counts as done only when CI is green, so **M0 is not done yet.** Local results are from Windows 11 only.
Walkthrough with full output: [docs/walkthroughs/m0.md](docs/walkthroughs/m0.md).

| Simulation metric | Value |
|---|---|
| Runs executed | 0 (simulator arrives in M2) |
| Mutant catch rate | Not measured |
| Shrunk regressions added | 0 |

## What M0 found

Reading the source and testing it turned up behaviour in Datasync 10.1.2 that shapes the whole design. All of it is pinned by tests and
explained in the [integration map](docs/design/datasync-integration.md).

- **A push that times out reports success**, with nothing sent and the change still queued (C24).
- **The write after a conflict resolver's merge is unconditional**, so a concurrent update can be silently lost (C10).
- **A poor signal alone can leave one device in a conflict with itself**, when the server applied a write but the response never arrived (C12, C25).
- **A row sharing the sync token's millisecond is never pulled** (C19).

As a result, [ADR-0012](docs/DECISIONS.md) chooses an append-only operation log with merging done on the server, rather than a client conflict resolver alone.

## Honest limits

What Leeway cannot guarantee is as important as what it can. These apply to the design, and will be proven or corrected as it is built.

- **Some rules cannot be guaranteed offline.** Each rule will be labelled as guaranteed offline, detected on reconnect, or requires a connection.
  A ticket scanned at two offline gates can only be *detected* on reconnect.
- **A device that never returns still holds its lease.** That quota is stranded until an admin writes it off, and if the device was only offline, its spends can then break the rule. They are reported, not hidden ([ADR-0005](docs/DECISIONS.md)).
- **A hybrid logical clock limits the damage from clock skew.** It cannot know which of two concurrent edits a person meant to win.
- **Only SQLite is tested.** Datasync's own docs advise against SQLite for production services, so server deployments on other databases are unverified ([ADR-0003](docs/DECISIONS.md)).
- **The M0 sample has none of Leeway's protections.** It deliberately shows Datasync's default behaviour, including the stuck conflicts above.

## Repository

| Path | Contents |
|---|---|
| `src/` | Library projects (scaffolds for now) |
| `samples/` | GateKeeper server and Avalonia desktop app |
| `tests/` | Unit, property, snapshot, analyzer, integration, and UI tests |
| `mutants/` | Deliberately broken implementations for simulator evaluation (from M2) |
| `docs/` | [Product](docs/PRODUCT.md), [architecture](docs/ARCHITECTURE.md), [decisions](docs/DECISIONS.md), [roadmap](docs/ROADMAP.md), [risks](docs/RISKS.md), [progress](docs/PROGRESS.md) |

## AI-assistance disclosure

This project is built with substantial AI assistance. Claude (Anthropic), through Claude Code, wrote the code, tests, and documentation so far under a maintainer's brief.
Claims here are meant to be checked: each comes with a command to run. Commits are authored by the maintainer, who is responsible for them, so this notice rather than commit metadata is the record of AI involvement.

## License

[MIT](LICENSE). Samples use only free, open source components. Building the Avalonia sample sends anonymous build telemetry unless `AVALONIA_TELEMETRY_OPTOUT=1` is set ([ADR-0011](docs/DECISIONS.md)).
