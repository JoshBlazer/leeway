# Datasync integration map

**Target:** `CommunityToolkit.Datasync.*` 10.1.2 (tag `10.1.2`, commit `8d9e284`), read from source.
**Evidence:** every behaviour marked `C..` or `S..` is pinned by a test of that ID in
`tests/Leeway.Integration.Tests/Datasync/`, running real HTTP against a real Kestrel server and real SQLite.
Items marked *source* come from reading the code and have no test.

```
dotnet test --project tests/Leeway.Integration.Tests
```

These are characterization tests, so a passing test means "Datasync still behaves this way", including
where that behaviour is a trap. If an upgrade breaks one, a design assumption has changed.

## The short version

Datasync's offline model is a **queue of whole-row writes, guarded by a version check**. That is a poor
carrier for per-field merging and a good carrier for an **append-only operation log**. Six findings drive the design:

| # | Finding | Consequence for Leeway |
|---|---|---|
| 1 | **A push that times out reports `IsSuccessful == true`** with nothing sent and the operation still queued (C24) | Never trust `PushResult` alone: check the queue. Bad signal and no signal behave differently (C02) |
| 2 | **The write after a resolver merge is unconditional** (C09), so a third writer in the window is silently lost (C10) | `IConflictResolver` cannot be the place a merge is finalized |
| 3 | **Completed work is recorded only at the end of a batch.** A mid-batch connection drop forgets successes (C13), and a lost response or timeout makes a device conflict with itself (C12, C25) | Delivery is at-least-once. Application must be idempotent |
| 4 | **Edits to a row are compacted** into one whole-row operation (C07) | The queue cannot carry intent. Leeway needs its own operation log |
| 5 | **On a millisecond-precision store, a row sharing the delta token's millisecond is never pulled** (C19) | Silent convergence failure on SQLite, which is all Leeway tests on. The server must stamp strictly increasing `UpdatedAt` |
| 6 | **The sync lock is process-wide**: queued local writes block during any push (C23) | An offline spend must not wait on the network, so it cannot go through a queued write |

These findings led to the design in [ADR-0012](../DECISIONS.md#adr-0012-carry-intent-in-an-append-only-operation-table-and-merge-on-the-server).

## Client extension points

| Extension point | Access | Behaviour (evidence) | Leeway use |
|---|---|---|---|
| `IConflictResolver`, `IConflictResolver<T>`, `AbstractConflictResolver<T>`, per entity or as default | Public | Called on 409/412 with deserialized client and server rows (C08). `Client` force-writes `Entity` with no `If-Match` (C09). `Server` adopts the server row (C14). `Default` leaves the operation `Failed` (C05, *source*) | `ServerWins` on the op table, where a 409 means "already applied" (C14). Not used to finalize merges |
| `OfflineDbContext.DatasyncOperationsQueue` | Public `DbSet` | Row fields can be edited, and a rebased row keeps `If-Match` (C11) | Compare-and-swap fallback for any whole-row conflict |
| `[DoNotSynchronize]` on a `DbSet` | Public | Same SQLite store, commits atomically with entity edits, never queued or pushed (C16) | Op log state, lease wallet, HLC state |
| `SaveChangesAsync(acceptAll, addToQueue)` | Public | `addToQueue: false` skips the queue and the sync lock (C23) | Fast local writes, projections |
| `[JsonIgnore]` on an entity column | Public | Not sent (C18), not overwritten by pull (C17) | Client-only columns on a synced row |
| `UseHttpClient`, `UseHttpClientFactory`, `HttpClientOptions.HttpPipeline` | Public | A `DelegatingHandler` sees every request (C20) and can inject faults (the probes rely on this) | Device identity header, connectivity gating |
| `SynchronizationProgress` event | Public | Exposes each pushed operation (C21) | Provisional status for the UI |
| `OfflineDbContext.PushAsync` | Public `virtual` | Can be wrapped | Wrap for the queue check from finding 1 |
| `PullAsync` | Public, **not** virtual | Refuses to run while the type has any non-completed operation (C06) | Keep op rows in a separate type so entity pulls are not blocked |
| `IPullOperationManager`, `IDeltaTokenStore`, `OperationsQueueManager` | **Internal** | Pull has no merge hook. It overwrites every non-`[JsonIgnore]` column (C17) | Not available. Treat pulled rows as server truth and re-apply unacknowledged local ops on top |

## Server extension points

| Extension point | Access | Behaviour (evidence) | Leeway use |
|---|---|---|---|
| `TableController<T>` `CreateAsync`, `ReplaceAsync`, and others | Public `virtual` | An override can change the request before the version check (S04) | Route an operation to the escrow authority |
| `IRepository<T>`, `EntityTableRepository<T>` | Public, `virtual` | Runs after the version check (S01, S03). Holds the `DbContext` | **Transactional home of op application:** dedupe, apply, and stamp in one `SaveChanges` |
| `IAccessControlProvider<T>` `PreCommitHookAsync` | Public | Can rewrite the entity before storage, and the device gets the result back (S02) | Validation and per-tenant checks |
| `PostCommitHookAsync`, `RepositoryUpdated` event | Public | Runs **after** commit, and a failure returns 500 though the write stands (S05) | Dashboard notifications only, never invariants |
| Duplicate create | Built in | 409 with the stored row (S06). `PreCommitHookAsync` runs first, so hooks must not have side effects | The idempotence signal for the op table |
| ASP.NET Core filters and middleware | Public | Normal pipeline (C20) | Reading the device identity header |

**Not interceptable:** a stale `If-Match` returns 412 before any hook or repository call (S03).
`ParseConditionalRequest` is internal. Only a controller override gets in first.

## Failure modes a caller must handle

| Situation | What `PushAsync` does | Evidence |
|---|---|---|
| Connection refused, DNS failure | **Throws** `HttpRequestException`, queue intact | C02 |
| `HttpClient.Timeout` expires | **Returns success**, operation still queued, and a following pull throws | C24 |
| Server returns 409/412, no resolver | Returns failure, operation `Failed`, pull blocked for that type | C05, C06 |
| Server applied it, connection lost before the response | Throws, and the retry self-conflicts with 412 (replace) or 409 (add) | C12, C15 |
| Server applied it, client timed out waiting | **Returns success**, and the retry self-conflicts. With the default resolver, a single device with no other writers is now stuck | C25 |
| Connection drops mid-batch | Throws. Earlier successes are not recorded on disk | C13 |

`PullAsync` differs: with no network it **returns** `IsSuccessful == false` with a local exception (C03).

The cause of the timeout case: push runs through a TPL Dataflow `ActionBlock`, which treats an
`OperationCanceledException` from its delegate as cooperative cancellation rather than a fault (*source:*
`QueueHandler<T>`; behaviour: C24).

A flaky test found this. The M0 exit check failed intermittently on a loaded machine, because a real timeout in
the sample reached this path. See `docs/walkthroughs/m0.md`.

## SQLite on the server

- SQLite cannot compare or order `DateTimeOffset`, and pull does both on `UpdatedAt`. Store Unix milliseconds
  through a value converter (`GateKeeperDbContext`), as Datasync's own test suite does.
- `BaseEntityTableData` declares no concurrency token, so the repository's version check is check-then-write
  (*source*). Add `IsConcurrencyToken()` on `Version`.
- Datasync's documentation says **not to use SQLite for production services**. Leeway uses it for samples and tests
  only (ADR-0003). Other providers are unverified, and see the honest limit in `docs/RISKS.md`.
- Datasync 10.1.2 resolves a vulnerable native SQLite transitively. See ADR-0008; C22 guards the fix.

## Other limits, from source

- `GetQueuedOperationsAsync` has **no `ORDER BY`**, so FIFO is not guaranteed even with one parallel operation. Leeway's
  op application must not depend on arrival order.
- `Failed` operations are retried on every push, indefinitely.
- `LockManager` is static, so several devices cannot sync concurrently in one process, and calling one client from inside
  another's push deadlocks. **The M2 simulator therefore models the protocol on `Leeway.Core` instead of running N real
  Datasync clients in-process.** Integration tests run devices sequentially.

## Upstream proposals (constraint 3)

Only the two clearest are filed: deterministic, independent of the database, and with no reasonable case for the current behaviour. The
others are design requests or narrower, so they are held to avoid flooding a small team. Keyword searches found no existing issue for U1 to U4 or U6, which does not prove there is none. Leeway works around each one regardless.

| # | Proposal | Evidence | Workaround | Upstream |
|---|---|---|---|---|
| U1 | A timed-out push should report failure, not success | C24, C25 | Check the queue after push | [#578](https://github.com/CommunityToolkit/Datasync/issues/578): **fixed upstream, unreleased** |
| U2 | Resolver `Client` result should send `If-Match` with the server version just received | C09, C10 | Merge on the server; rebase (C11) | [#579](https://github.com/CommunityToolkit/Datasync/issues/579): **fixed upstream, unreleased** |
| U3 | Persist each operation's completion as it happens | C13 | Idempotent op table | Held. The #578 fix also persists progress when a push fails, which may resolve this |
| U4 | Close the same-millisecond delta gap, for example `ge` with client-side dedupe | C19 | Strictly increasing server stamps (ADR-0013) | Held: see below |
| U5 | Order queued operations by `Sequence` | *source* | Order-independent application | Held |
| U6 | Scope the sync lock per context or store, not per process | C23 | Unqueued local writes | Held |
| U7 | Expose a pull hook | *source* | Re-apply local ops after pull | Held |

**Why U4 is held:** the client truncates its delta token to milliseconds (`DefaultDeltaTokenStore`, *source*). On a store with finer `UpdatedAt` precision, a same-millisecond row should still compare greater than the token, so the gap looks specific to millisecond-precision stores, such as SQLite with the converter above. Datasync already advises against SQLite for production. This reasoning about other stores is from source only, untested. C19 itself is tested on SQLite.

## A race in Datasync's lock

`AsyncLockDictionary` disposes an entry when its count reaches 0, but the check is not atomic with another caller's `GetOrAdd` and increment. That caller can be left with a disposed lock. From the code, it also looks possible for two callers to hold the "exclusive" lock at once, though that has not been observed. Reported as [#596](https://github.com/CommunityToolkit/Datasync/issues/596), and fixed by the maintainer in [PR #599](https://github.com/CommunityToolkit/Datasync/pull/599), not yet released.

It needs several contexts syncing in one process. It appeared once, in CI, on a probe test. A tight loop did not reproduce it, which fits the analysis, since a busy lock rarely reaches 0.

**Leeway's defence:** `LeewaySyncGate` lets one sync run at a time across every client in the process (`Clients_in_one_process_sync_one_at_a_time`). So a client stuck on a slow network delays other clients' syncs in that process, but never their local writes. Using `OfflineDbContext` directly bypasses this, so the integration test assembly runs sequentially.

## When upgrading past 10.1.2

The maintainer fixed #578 within hours, in [PR #581](https://github.com/CommunityToolkit/Datasync/pull/581), merged 2026-11-05 and not yet released. Read from its description and diff, not tested against a build of it:

- A timed-out push will **throw `TimeoutException`** instead of reporting success. `LeewayClient` and the sample already treat that as offline, and a test covers it.
- Completed operations will be saved before the exception is rethrown, so a mid-batch drop should no longer forget them.
- **Probes C13, C24, and C25 will then fail. That is what they are for.** Update them to the new behaviour, and keep Leeway's queue check, because it is still correct and harmless.

The maintainer has said they will try to release a 10.2.0 soon. Once it includes these fixes, `LeewaySyncGate` and the sequential test assembly can be reconsidered, though the gate does no harm.

The maintainer also fixed #579, in [PR #582](https://github.com/CommunityToolkit/Datasync/pull/582), adding a conditional `ConflictResolutionResult.Merged`. That does not change ADR-0012, because a resolver still sees only rows and not intent, so a counter or escrow could not use it. `Client` still force-writes, so C09 and C10 should keep passing. It could help an application with plain whole-row entities.

## Questions from M0, and what M3 and M4 did

1. **Moving ops into the Datasync queue.** Solved: a spend commits to a `[DoNotSynchronize]` outbox with `addToQueue: false`, and sync copies
   outbox rows into the queued op table just before pushing. Tested by `A_spend_completes_while_a_push_is_stuck_on_the_network`.
2. **Reading a row while its ops are in flight.** Solved: see "Local view" in `escrow-protocol.md` and "settling deltas" in `merge-semantics.md`.
3. **Op table growth.** **Still open.** The server, and each device's outbox, keep every operation forever. A retention policy has to keep replays harmless.
