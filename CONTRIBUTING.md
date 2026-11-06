# Contributing to Leeway

For where things stand, read `docs/PROGRESS.md` and `docs/ROADMAP.md`.

## House rules

- **"Done" means a command proves it.** Update the README status table in the same commit as the work. Run the command and show its output.
- **Design notes before code** for hard modules (escrow protocol, HLC, merge semantics, simulator, generator), with signatures and failing tests first.
- **Decisions go in `docs/DECISIONS.md`.** Each new runtime dependency needs an entry. Raise anything irreversible before doing it.
- **Honest limits:** document what cannot be guaranteed offline as prominently as what can.
- **Project rules:** zero cost (check licences, see ADR-0009); extend, do not fork, Datasync; SQLite in samples and on devices, with MySQL allowed for server tests only (ADR-0019); MIT.
- Core logic stays pure and fully tested before integration. `Leeway.Core` has no Datasync dependency.
- Small conventional commits. Record progress in `docs/PROGRESS.md`.
- Publishing packages needs approval.

## Commands

The SDK may be user-local: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`.

```
dotnet build Leeway.slnx
dotnet test --solution Leeway.slnx
dotnet test --project tests/Leeway.Integration.Tests
powershell -ExecutionPolicy Bypass -File scripts/m6-demo.ps1    # Windows, after a build
python scripts/make-demo-gif.py                                 # Rebuilds the README's GIF
dotnet run --project mutants/Leeway.Mutants -c Release -- catch
dotnet run --project mutants/Leeway.Mutants -c Release -- soak 10000 1
python scripts/adapter-sabotage.py                              # Clean tree. Slow: about an hour
gh workflow run adapter-sabotage.yml                            # The same, on GitHub: about six minutes
python scripts/adapter-sabotage.py --hardening-only             # Much faster
LEEWAY_TEST_MYSQL="Server=127.0.0.1;Port=3306;User=root;Password=x" dotnet test --project tests/Leeway.Integration.Tests
python scripts/package-check.py                                 # Real packages, in an outside project
```

Tests run on Microsoft Testing Platform (ADR-0010). `dotnet test <path>` and VSTest options do not apply.

## Things that will bite you

- Read `docs/design/datasync-integration.md` before touching sync code. In particular, a retry after a timeout can conflict with the device's own write (C25), and a resolver's merged write is unconditional (C10).
- The `tests/Leeway.Integration.Tests/Datasync` probes pin upstream behaviour, traps included. If one fails after an upgrade, re-read the design. Do not simply edit the test.
- Datasync's sync lock is process-wide. Running one client inside another's push deadlocks.
- Do not upgrade Verify past 32.x (ADR-0009) or suppress NuGet advisories (ADR-0008).
- A snapshot test with no generated output passes without comparing anything. Check that a `*.verified.*` file exists.
- When a test fails intermittently, keep the full output and find the cause. That is how C24 was found.
- Soak the correct implementation before believing a mutant catch rate. A bug in the correct code looks like a 100% catch rate.
- An upstream fix can leave a guard that nothing exercises. After an upgrade, re-run the sabotage cases: one that stops being caught may mean that.
- A test passing first time proves little. Check that it can fail: the first sabotage run found three protections no test really covered.
- A killed sabotage run leaves the sabotage in the source and in the binaries. This has happened. The script now refuses a changed tree: check `git diff`, then rebuild.
- Sabotage tools must fail loudly if no tests ran. One of ours once reported four false "survived" results for that reason.
- A snapshot is only a promise about text: read a new one before accepting it.
- Look at rendered output too. The first demo GIF passed every assertion and had a blank dashboard.
- Do not assume a device's operations arrive in order. That made a test pass by luck.
- On MySQL, the first plain read in a transaction fixes its snapshot, so take the lock before any read.
- Change entities only through `LeewayClient`. Direct EF saves are refused, because a queued whole-row write would block pulls (C06).
