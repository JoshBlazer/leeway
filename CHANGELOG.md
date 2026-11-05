# Changelog

No packages have been published.

## Unreleased

### Added

- `Leeway.Core`: counters, add-wins sets, HLC registers, stamp admission, escrow ledger, operation log, and merge schema (M1).
- `Leeway.Simulation` and `mutants/Leeway.Mutants`: deterministic simulator, shrinker, mutation corpus, and nightly soak (M2).
- `Leeway.Datasync.Server` and `Leeway.Datasync.Client`: escrow end to end (M3) and per-field merging (M4) over Datasync.
- `Leeway.Generators` and `Leeway.Analyzers`: a generated schema and typed API, and rules LEE001 to LEE006 with code fixes (M5).
- `scripts/adapter-sabotage.py`: checks that tests notice when each adapter protection is broken.
- `scripts/package-check.py`: checks the real NuGet packages from an outside project.
- Solution scaffold, CI workflow, and project documentation.
- GateKeeper demo: server, dashboard, and Avalonia desktop app, using Leeway through its public API (M6). It replaces the M0 spike.
- A draft write-up in `docs/writeup.md`.
- Characterization tests of Datasync 10.1.2 and the extension-point map in `docs/design/datasync-integration.md`.
