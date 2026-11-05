# Changelog

No packages have been published.

## Unreleased

### Added

- `Leeway.Core`: counters, add-wins sets, HLC registers, stamp admission, escrow ledger, operation log, and merge schema (M1).
- `Leeway.Simulation` and `mutants/Leeway.Mutants`: deterministic simulator, shrinker, mutation corpus, and nightly soak (M2).
- `Leeway.Datasync.Server` and `Leeway.Datasync.Client`: escrow end to end (M3) and per-field merging (M4) over Datasync.
- `scripts/adapter-sabotage.py`: checks that tests notice when each adapter protection is broken.
- Solution scaffold, CI workflow, and project documentation.
- M0 spike: `GateKeeper.Server` and `GateKeeper.Desktop`, syncing one entity offline with Datasync's default behaviour.
- Characterization tests of Datasync 10.1.2 and the extension-point map in `docs/design/datasync-integration.md`.
