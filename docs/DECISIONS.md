# Decision records

Status is **Accepted** or **Proposed** (awaiting maintainer review).

## ADR-0001: Target .NET 10

**Status:** Accepted.
**Context:** The brief says to match Datasync's supported .NET version.
**Evidence:** The `CommunityToolkit.Datasync.Client` and `.Server` 10.1.2 nuspecs contain only a `net10.0` dependency group (checked 2026-11-04).
**Decision:** `net10.0` everywhere, except generators and analyzers, which must be `netstandard2.0` to load in the compiler. SDK pinned in `global.json` at 10.0.403.

## ADR-0002: Extend Datasync, do not fork it; pin 10.1.2

**Status:** Accepted.
**Decision:** Use public extension points only. Where they fall short, document it, propose it upstream, and work around it. Datasync is pinned centrally to 10.1.2, the latest release at the time.
**Consequence:** Upgrades are deliberate. The probe tests show what an upgrade changed. See `docs/design/datasync-integration.md`.

## ADR-0003: SQLite only, for client and server, in all samples and tests

**Status:** Accepted.
**Decision:** No other database in samples or tests.
**Honest limit:** Datasync's own docs warn against SQLite for production services. Passing tests here say nothing about other server databases, so Leeway will state which server providers it has verified, which at first is only this one.

## ADR-0004: Hybrid logical clocks for last-writer-wins

**Status:** Accepted. Built in M1. See `docs/design/hlc.md` and ADR-0014.
**Context:** With wall clocks, a device whose clock is ahead wins every merge.
**Decision:** LWW registers order by HLC (physical, logical, device ID), and receivers advance their clock past anything they see.
**Honest limit:** An HLC preserves causality. It cannot tell which of two concurrent writes a human meant to win, and on its own it lets a device with a far-future clock drag everyone forward. ADR-0014 bounds that.

## ADR-0005: Stranded quota is written off explicitly by an admin

**Status:** Accepted.
**Context:** A device that never returns still holds its lease. Rights cannot be safely revoked from an offline device, and device clocks cannot be trusted.
**Decision:** Lease expiry is advisory. Write-off is an explicit admin action with documented risk: if the device was only offline, its spends can exceed the invariant, and they are then reported as violations rather than hidden.

## ADR-0006: MIT license

**Status:** Accepted.

## ADR-0007: Package IDs and repository name

**Status:** Accepted, with a caveat for the maintainer.
**Evidence (2026-11-04):** `api.nuget.org` returns 404 for `Leeway`, `Leeway.Core`, `Leeway.Datasync`, `Leeway.Datasync.Client`, `Leeway.Datasync.Server`, `Leeway.Generators`, `Leeway.Analyzers`, and `Leeway.Simulation`.
**Caveats:**
- IDs are free, not reserved. The NuGet profile `Leeway` owns `LeewayUtils` and `DintinctTool`, and an unrelated `LeeWay.Ensure.ControllerAttributes` shares the prefix, so reserving the `Leeway.` prefix may not be possible. Publishing early placeholders is the only sure claim.
- `github.com/leeway` is a dormant, unrelated user account with no public repos. A repository named `leeway` under a personal account or a new organization is available. `leeway-dotnet` and `leewaydotnet` were free as organization names.
**Decision:** Package IDs as in the brief. The repository is `JoshBlazer/leeway`. `Leeway.Datasync` stays reserved for a possible metapackage at M6. `Leeway.Simulation` is not packable for now.

## ADR-0008: Pin transitive packages with security advisories

**Status:** Accepted.
**Context:** Datasync 10.1.2 resolves `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (GHSA-2m69-gcr7-jv3q, CVE-2025-6965, high). Warnings-as-errors correctly failed the build. The analyzer test harness also resolved a vulnerable `System.Security.Cryptography.Pkcs` 9.0.6.
**Decision:** Enable central transitive pinning, and pin `SQLitePCLRaw.*` to 2.1.14 and `Pkcs` to 10.0.14. Do not suppress the advisory.
**Proof:** Probe C22 asserts the loaded native SQLite is at least 3.50.2.
**Consequence:** Leeway's consumers are not protected by our pins, because pins are not inherited through a package. The packages must document this until upstream moves. `NU1900`, which means the advisory feed was unreachable, is not an error.

## ADR-0009: Stay on Verify 32.x, the last MIT release line

**Status:** Accepted.
**Context:** The brief names Verify. Verify 33 and later are under an Open Source Maintenance Fee EULA, covering releases after 2026-09-01, and fail the build (SC021) unless the build declares sponsorship, a time-limited exemption, or an acknowledged breach. An open source project without revenue is exempt, but must declare this in the build and renew it at least yearly.
**Decision:** Pin `Verify` and `Verify.XunitV3` at 32.0.1, which are MIT.
**Why not the exemption:** It is a licence declaration the maintainer should make, not a tooling default. It also travels with the repo, and a contributor at a company with revenue could not truthfully rely on it. That matters for a project pitched to .NET teams, and for hard constraint 1.
**Consequence:** No Verify updates, including fixes, unless this is revisited. The alternative is plain assertions over generated source.

## ADR-0010: xUnit v3 4.x on Microsoft Testing Platform; drive Avalonia headless directly

**Status:** Accepted.
**Context:** `FsCheck.Xunit.v3` needs xUnit v3 4.x, which runs only on Microsoft Testing Platform. `Avalonia.Headless.XUnit` 12.1.5 declares `>= 3.2.2` but fails on 4.x with `MissingMethodException`.
**Decision:** One xUnit version across the repo, `global.json` selects the platform, and UI tests use `HeadlessUnitTestSession` from plain `[Fact]`s.
**Analyzer tests:** The Roslyn harness's default reference assemblies are downloaded from NuGet at test time, which failed intermittently. Tests use `OfflineReferences`, which compiles against the running .NET 10 runtime. Analyzer results can therefore depend on the runtime version, which `global.json` constrains.
**Commands:** `dotnet test --solution Leeway.slnx` and `dotnet test --project <path>`.

## ADR-0011: Dependency register

**Status:** Accepted. Each new runtime dependency adds a line here.

| Package | Used by | Licence | Why |
|---|---|---|---|
| `CommunityToolkit.Datasync.Client` 10.1.2 | `Leeway.Datasync.Client`, samples | MIT | The product extends it. Brings EF Core and SQLite |
| `CommunityToolkit.Datasync.Server` 10.1.2 | `Leeway.Datasync.Server`, samples | MIT | Same |
| `CommunityToolkit.Datasync.Server.EntityFrameworkCore` 10.1.2 | `Leeway.Datasync.Server`, samples | MIT | The op table's transactional repository (ADR-0012). This makes the server package depend on EF Core |
| `Microsoft.EntityFrameworkCore.Sqlite` 10.0.14 | Samples | MIT | ADR-0003 |
| `Avalonia`, `.Desktop`, `.Themes.Fluent`, `.Fonts.Inter` 12.1.5 | Sample only | MIT | Demo UI. No paid components |

`Leeway.Core` has no runtime dependencies. `CommunityToolkit.Mvvm` was deliberately not added: a few lines of view model base suffice.

**Build telemetry disclosure:** Avalonia brings `Avalonia.BuildServices`, which sends anonymous build-time data (hashed project and machine names, versions, OS). Nothing runs in deployed apps, and it affects only those building the samples. CI opts out with `AVALONIA_TELEMETRY_OPTOUT=1`, and contributors can set the same variable.

## ADR-0012: Carry intent in an append-only operation table, and merge on the server

**Status:** Accepted by the maintainer, 2026-11-04, and built in M3 and M4. It changes an emphasis in the brief.
**Context:** Brief §5 describes a client-side "field-semantic conflict resolver". M0 found this cannot be sufficient by itself:
- A resolver's merged write is unconditional, so a concurrent update is silently lost (C09, C10). For a counter or escrow balance, this is precisely the failure Leeway exists to prevent.
- A resolver sees only two row states. A PN-counter or OR-set needs operations or per-device state, and compaction removes them (C07).
- Retries are inherent (C12, C13, C24, C25), and whole-row replace is not idempotent. Under Datasync's default, a poor signal alone can strand a device in a conflict with itself (C25).

**Decision:**
1. A write records an **operation** with a client-generated UUID in an append-only Datasync table (C14). A retry is a 409, which `ServerWins` turns into "already applied".
2. The server applies each operation **in one transaction** in a custom repository: dedupe, merge into the entity's per-field state, enforce invariants, stamp. Hooks are not transactional (S05).
3. Entity rows are read-only projections from the client's point of view. They are pulled, and never written through the queue, so they cannot conflict or block pulls (C06). The client overlays its unacknowledged ops (C17).
4. A client `IConflictResolver` remains for the op table and for plain whole-row entities. Where a merge must happen on a whole-row write, it uses the compare-and-swap rebase (C11).

**Fit with the brief:** Rides on Datasync's transport, needs no fork, and meets M4's exit check ("merge per field instead of whole-row wins"). The difference from the brief is that the merge is authoritative on the server, while the client computes the same merge only for its local view.
**Alternatives:** A resolver alone is unsafe as above. Controller-level merge of whole rows (S04) cannot recover intent. A custom endpoint breaks the "no custom transport" non-goal.
**Costs:** Op table growth, plus two tables per concept. Apps can no longer simply edit an entity and call `SaveChanges`: the generated API must make that feel natural.

## ADR-0013: Server stamps strictly increasing UpdatedAt

**Status:** Accepted by the maintainer, 2026-11-04, and built in M3. Goes with ADR-0012.
**Context:** The pull filter is `updatedAt gt <token>`, and SQLite stores `UpdatedAt` in milliseconds, so a later commit in the token's millisecond is never delivered (C19). That breaks simulator property 2 (convergence) with no error anywhere.
**Decision:** Leeway's server repository stamps `max(now, lastStamp + 1ms)` inside the write transaction. SQLite's single writer makes this sufficient.
**Honest limit:** This relies on commit order matching stamp order. A server database with concurrent writers needs more care, and that is another reason ADR-0003 limits what is verified.

## ADR-0014: Stamp admission clamps to the drift limit and keeps a device's stamps in sequence order

**Status:** Accepted. It satisfies the maximum-drift requirement of ADR-0004.
**Context:** The simulator disproved two earlier designs, each passing every unit test written at the time.
**Decision:** The server clamps a stamp to `now + maxDrift`, places it between the operation's neighbours in the device's sequence, then raises it above whatever the device had seen. Each operation carries `SettledBelow`, which bounds the state kept. Details and the rejected designs are in `docs/design/hlc.md`.
**Consequence:** `Operation` carries `Observed`, `Sequence`, and `SettledBelow`, and the server stores some state per device.

## ADR-0015: Until the generator exists, read attributes by reflection and store sets as JSON text

**Status:** Accepted, as a stopgap for M5.
**Context:** The brief's sketch has `ISet<string> Flags` and `zone.TrySpendAsync(...)`. M4 comes before the generator.
**Decision:** `MergeSchema` reads the attributes by reflection. An `[AddWinsSet]` property is a `string` holding a JSON array. Spends and edits go through `LeewayClient`, as in `client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1)`.
**Consequence:** The developer experience is rougher than the sketch, and reflection rules out NativeAOT for now. M5 should generate the schema and the friendlier API. `ISet<string>` has not been tried against EF Core and OData.

