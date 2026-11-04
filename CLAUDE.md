# Leeway: working agreement

Start each session by reading `docs/PROGRESS.md`, this file, and the current milestone in `docs/ROADMAP.md`.

## House rules

- **"Done" means a command proves it.** Update the README status table in the same commit as the work. Run the command and show its output.
- **Design notes before code** for hard modules (escrow protocol, HLC, merge semantics, simulator, generator), with signatures and failing tests first.
- **Decisions go in `docs/DECISIONS.md`.** Each new runtime dependency needs an entry. If a decision is irreversible or contradicts the brief, stop and ask.
- **Honest limits:** document what cannot be guaranteed offline as prominently as what can.
- **Hard constraints:** zero cost (check licences, see ADR-0009); extend, do not fork, Datasync; SQLite only in samples and tests; MIT.
- Core logic stays pure and fully tested before integration. `Leeway.Core` has no Datasync dependency.
- Small conventional commits. Append to `docs/PROGRESS.md` at the end of each session. Stop at the end of each milestone and wait for review.
- Filing upstream issues, pushing, and publishing packages need the maintainer's approval.

## Commands

The SDK may be user-local: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`.

```
dotnet build Leeway.slnx
dotnet test --solution Leeway.slnx
dotnet test --project tests/Leeway.Integration.Tests
powershell -ExecutionPolicy Bypass -File scripts/m0-demo.ps1    # Windows, after a build
```

Tests run on Microsoft Testing Platform (ADR-0010). `dotnet test <path>` and VSTest options do not apply.

## Things that will bite you

- Read `docs/design/datasync-integration.md` before touching sync code. In particular, `PushResult.IsSuccessful` can be true when nothing was sent (C24), and a resolver's merged write is unconditional (C10).
- The `tests/Leeway.Integration.Tests/Datasync` probes pin upstream behaviour, traps included. If one fails after an upgrade, re-read the design. Do not simply edit the test.
- Datasync's sync lock is process-wide. Running one client inside another's push deadlocks.
- Do not upgrade Verify past 32.x (ADR-0009) or suppress NuGet advisories (ADR-0008).
- A snapshot test with no generated output passes without comparing anything. Check that a `*.verified.*` file exists.
- When a test fails intermittently, keep the full output and find the cause. That is how C24 was found.
