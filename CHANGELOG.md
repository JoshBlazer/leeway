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
- A write-up in `docs/writeup.md`.
- Server support for MySQL, with a cross-process write lock and a case-sensitivity check, tested in CI.
- Upgraded to Datasync 10.2.0. A timed-out push now throws, which Leeway reports as offline. The SQLite security pins are gone, because upstream fixed the advisory, so consumers get the fix too.
- Device authentication, which is a required decision at startup, plus retention, discarding refused operations, entity deletion, and first-come ordering within a device.
- Characterization tests of Datasync 10.1.2 and the extension-point map in `docs/design/datasync-integration.md`.
