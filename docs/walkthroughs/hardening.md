# Walkthrough: hardening

After M6, this works through the roadmap's "before real use" list. Design: `docs/design/hardening.md`.

```
dotnet test --project tests/Leeway.Integration.Tests -c Release
dotnet run --project mutants/Leeway.Mutants -c Release -- catch
python scripts/adapter-sabotage.py --hardening-only
```

| Item | State | Proof |
|---|---|---|
| Device authentication | Done, as a hook the application fills | `HardeningTests`, 6 tests |
| Retention | Done for operations, not for tombstones | `HardeningTests`, a simulator test, and a mutant |
| Discarding a refused operation | Done | `HardeningTests`, 3 tests |
| Deleting entities | Done, delete wins | `HardeningTests`, 2 tests |
| First-come ordering | Done, per device | `HardeningTests`, 2 tests, and the M6 test |
| A production server database | MySQL 8.4 tested, at the maintainer's request (ADR-0019) | Every end-to-end test, plus `MySqlServerTests`, with `LEEWAY_TEST_MYSQL` set |
| Datasync 10.2.0 | Done | Probes C13, C24, and C25, and "Upgrading to 10.2.0" in the integration map |

## Authentication is secure by default

The server now refuses to start unless the application chooses:

```csharp
services.AddLeewayServer<AppDb>(o =>
{
    o.Entity<Zone>();
    o.AuthenticateDevice = http => http.User.Identity?.Name;
});
```

This is a breaking change for anyone who had been running with no setting, which at this stage is nobody. Samples and tests opt out by setting `AllowUnauthenticatedDevices`, which reads as what it is. **GateKeeper is a demo, and runs with authentication off.**

## Retention is the risky one

The op table is what makes a retry harmless. So pruning uses what the device itself reported about what it has been told, and the simulator has a `Prune` event and a mutant that ignores this:

```
PruneIgnoresAcknowledgements        4    72 ->    3  P3: d1-1 was applied 2 times.
Catch rate: 19 of 19 (100%), searching up to 3000 seeds each.
Runs: 100000, start seed: 1, failures: 0, time: 192.0s
```

## Do the tests catch anything?

They passed first time, so each protection was broken on purpose, with these results from `scripts/adapter-sabotage.py --hardening-only`:

```
A device can send operations as another device: CAUGHT
Entities can be read with no identity: CAUGHT
A device can read every lease: CAUGHT
A replay reveals another device's operation: CAUGHT
Startup allows no decision on authentication: CAUGHT
Server pruning ignores acknowledgements: CAUGHT
Server forgets to record a device's progress: CAUGHT
Client pruning removes unsettled operations: CAUGHT
Any failed operation can be discarded: CAUGHT
Discarding does not give the wallet back: CAUGHT
The server ignores a delete: CAUGHT
The client sends everything at once, unordered: CAUGHT
```

Unlike M3's first run, none survived. One first gave no result, because the sabotaged code did not compile, and the script correctly refused to count that either way.

## MySQL

```
LEEWAY_TEST_MYSQL="Server=127.0.0.1;Port=3306;User=root;Password=x" dotnet test --project tests/Leeway.Integration.Tests
```

All the existing end-to-end tests passed on MySQL with no changes to them. The value was in what SQLite could never show. `MySqlServerTests` runs two servers on one database, and found two real bugs, a deadlock and a stale first write, described in `docs/design/hardening.md`. With no cross-process lock, those tests fail.

With `LEEWAY_TEST_MYSQL` unset, those tests are skipped, which is why a run with no MySQL reports skips.

## Limits

- **Authentication gives identity, not honesty.** A modified client on a real device can still report spends it made up. There is no per-entity access control, so any authenticated device can read and edit any entity.
- **The operation's ID is unique across devices.** A device replaying another's ID gets a 400, not a stored row, but it does learn that the ID exists. IDs are random UUIDs, so this should not matter.
- **Set tombstones and counter entries still grow.** Compacting them needs to know that every device has seen them, and a device can stay offline forever.
- **Discarding an edit does not fix the local view** until the server next changes that row.
- **Deletion cannot be undone,** and an edit racing a delete is lost. The simulator does not model deletion.
- **Ordering is per device.** Two gates still race, in the order they reconnect. A push now makes one round trip per operation, so sync over real latency will be slower. That has not been measured.
- **The sabotage script's full run takes about an hour locally, and about six minutes on GitHub.** On Datasync 10.2.0, the manual workflow caught 19 of 19, and each CI run catches the 2 MySQL cases.
