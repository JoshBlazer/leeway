# Design note: hardening

The "before real use" list from the roadmap. Tests: `tests/Leeway.Integration.Tests/Leeway/HardeningTests.cs`, and the simulator for retention.

## MySQL, and more than one server

See ADR-0019. Two bugs that SQLite could never show were found on the way:

- **A lock-upgrade deadlock.** `INSERT IGNORE` on an existing row takes a shared lock, so two writers both held shared and both wanted exclusive. Now it locks first, and inserts only if the row is missing.
- **A stale first write.** A plain read before the lock fixes a MySQL transaction's snapshot at its default isolation level. The collation check was such a read, so each server's first write worked from old data. It now runs after the lock.

CI breaks the lock and the collation check on purpose on each run, and the tests notice both.

**Not tested:** separate machines (the tests run two servers in one OS process, each with its own write gate), replicas and failover, write throughput under the single lock, or any other database.

## Device authentication

```csharp
services.AddLeewayServer<AppDb>(o =>
{
    o.Entity<Zone>();
    o.AuthenticateDevice = http => http.User.Identity?.Name;   // Null means not authenticated.
});
```

**Secure by default:** startup throws unless the app sets `AuthenticateDevice`, or explicitly sets `AllowUnauthenticatedDevices = true`. A forgotten setting should not look like a working deployment.

With it set:

| Request | Rule |
|---|---|
| No identity | 401, on every Leeway table |
| An operation whose `Device` is not the caller | 401 |
| A replay of another device's operation ID | 400, without revealing the stored row |
| Reading leases | Only the caller's own |
| Reading entities | Any authenticated device. Per-entity access is the application's job |

Leeway takes a device identity from the app, and does not issue or validate credentials. A stolen device is still that device. **A modified client on a real device can still report spends it made up.** That shows as an overdraft, or as consumption the venue never saw.

## Retention

The risk: **the op table is what makes a replay harmless, so deleting a row a device might still retry would apply it twice.**

Each operation already carries `SettledBelow`, the device's oldest unacknowledged sequence. The device has its answer for everything below that, so it will never send those again. The server keeps the highest value per device, and `PruneOperationsAsync` deletes only rows below it. A device that has gone quiet keeps its rows until it returns.

`client.PruneAsync(keepLatest)` removes acknowledged and settled outbox rows, keeping recent ones so that outcomes can still be looked up.

The simulator gets a `Prune` event, and a mutant that prunes without this check.

**Not solved:** set tombstones and per-replica counter entries still grow, because compacting them needs to know that every device has seen them, and a device can stay offline indefinitely. Device-level `Admitted` entries and ledgers also stay.

## Discarding a refused operation

A refused operation (a 4xx that is not a conflict) is retried on every push. `client.RefusedOperationsAsync()` lists them. `client.DiscardRefusedAsync(id)` removes one and undoes its effect on the wallet.

It refuses anything else, because an operation that timed out or got a 5xx may have been applied.

**Limit:** a discarded merge's delta stays in the row's local view until the server next changes that row, because a merged state cannot be un-merged.

## Deleting entities

`client.DeleteAsync<T>(id)` writes a `$deleted` register, merged like any other. The server sets Datasync's soft-delete flag from it, and devices drop the row on their next pull.

**Delete wins.** An edit racing a delete is merged but stays invisible, with no way to restore it yet. That suits most field data, and it is wrong for some domains, as with add-wins sets. Deleting an entity leaves its ledger, so spends made offline are still recorded.

The simulator does not model deletion.

## First-come ordering

Datasync's queue has no `ORDER BY`, so a device's provisional spends competed in arbitrary order. The client now copies one operation at a time into the queue, in sequence order, pushes it, and stops at the first that does not get an answer.

**This is per device.** Two gates still race each other, in the order they reconnect. Reordering remains possible at the network level in principle, and the protocol stays safe under it, as the simulator shows. This only improves fairness, never safety.

**Cost:** each operation gets its own push. A refused operation still in the queue is retried on each of those, so discard it promptly.
