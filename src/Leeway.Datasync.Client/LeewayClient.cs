using System.Linq.Expressions;
using System.Reflection;
using CommunityToolkit.Datasync.Client.Offline;
using Leeway.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OperationKind = Leeway.Core.OperationKind;

namespace Leeway.Datasync.Client;

/// <summary>What a spend amounted to.</summary>
public enum SpendStatus
{
    /// <summary>Within the lease. It is guaranteed, with or without signal.</summary>
    Confirmed,

    /// <summary>Beyond the lease. The server decides on reconnect, and it may be rejected.</summary>
    Provisional,
}

/// <summary>The result of a spend.</summary>
/// <param name="Status">Confirmed or provisional.</param>
/// <param name="OperationId">Use this to look up the outcome.</param>
public sealed record SpendResult(SpendStatus Status, string OperationId);

/// <summary>How a sync went.</summary>
public enum SyncState
{
    /// <summary>Everything was sent and pulled.</summary>
    Synced,

    /// <summary>The server could not be reached, or did not answer in time. Nothing is lost, and sync can be retried.</summary>
    Offline,

    /// <summary>The server refused one or more operations, and will again. This needs attention.</summary>
    Failed,
}

/// <summary>The result of a sync.</summary>
/// <param name="State">How it went.</param>
/// <param name="RejectedOperations">Operations the server refused.</param>
public sealed record SyncReport(SyncState State, int RejectedOperations = 0);

/// <summary>An operation the server clearly refused, which will not succeed if retried.</summary>
/// <param name="OperationId">The operation's ID.</param>
/// <param name="Kind">What it does.</param>
/// <param name="EntityType">The entity type.</param>
/// <param name="EntityId">The entity ID.</param>
/// <param name="StatusCode">The HTTP status the server gave.</param>
public sealed record RefusedOperation(string OperationId, OperationKind Kind, string EntityType, string EntityId, int StatusCode);

/// <summary>A set of field changes to an entity, applied as one operation.</summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class EntityEdit<T> where T : OfflineEntity
{
    private readonly MergeSchema schema = MergeSchema.For(typeof(T));
    private readonly string device;
    private readonly HybridTimestamp stamp;
    private readonly string operationId;
    private MergeDocument view;
    private int tags;

    internal EntityEdit(MergeDocument view, string device, HybridTimestamp stamp, string operationId)
    {
        this.view = view;
        this.device = device;
        this.stamp = stamp;
        this.operationId = operationId;
    }

    internal MergeDocument Delta { get; private set; } = MergeDocument.Empty;

    /// <summary>Sets a [LastWriterWins] field.</summary>
    /// <typeparam name="TValue">The field's type.</typeparam>
    /// <param name="field">The field.</param>
    /// <param name="value">The new value.</param>
    /// <returns>This edit.</returns>
    public EntityEdit<T> Set<TValue>(Expression<Func<T, TValue>> field, TValue value)
    {
        MergeField target = Field(MergeSchema.FieldName(field), FieldKind.LastWriterWins);
        return Apply(MergeDocument.Empty.WithRegister(target.Name, new LwwRegister(MergeSchema.EncodeValue(value, target.ValueType), this.stamp)));
    }

    /// <summary>Adds to a [Counter] field.</summary>
    /// <param name="field">The field.</param>
    /// <param name="amount">The amount, which may be negative.</param>
    /// <returns>This edit.</returns>
    public EntityEdit<T> Add(Expression<Func<T, object?>> field, long amount)
    {
        string name = Field(MergeSchema.FieldName(field), FieldKind.Counter).Name;
        return Apply(MergeDocument.Empty.WithCounter(name, this.view.Counter(name).AddDelta(this.device, amount)));
    }

    /// <summary>Adds an element to an [AddWinsSet] field.</summary>
    /// <param name="field">The field.</param>
    /// <param name="element">The element.</param>
    /// <returns>This edit.</returns>
    public EntityEdit<T> AddToSet(Expression<Func<T, string?>> field, string element)
    {
        string name = Field(MergeSchema.FieldName(field), FieldKind.AddWinsSet).Name;
        return Apply(MergeDocument.Empty.WithSet(name, this.view.Set(name).AddDelta(element, $"{this.operationId}:{this.tags++}")));
    }

    /// <summary>Removes an element from an [AddWinsSet] field. A concurrent add that this device has not seen still wins.</summary>
    /// <param name="field">The field.</param>
    /// <param name="element">The element.</param>
    /// <returns>This edit.</returns>
    public EntityEdit<T> RemoveFromSet(Expression<Func<T, string?>> field, string element)
    {
        string name = Field(MergeSchema.FieldName(field), FieldKind.AddWinsSet).Name;
        return Apply(MergeDocument.Empty.WithSet(name, this.view.Set(name).RemoveDelta(element)));
    }

    private EntityEdit<T> Apply(MergeDocument delta)
    {
        Delta = Delta.Merge(delta);
        this.view = this.view.Merge(delta);
        return this;
    }

    private MergeField Field(string name, FieldKind kind)
        => this.schema.Fields.TryGetValue(name, out MergeField? field) && field.Kind == kind
            ? field
            : throw new ArgumentException($"{typeof(T).Name}.{name} is not a {kind} field.", nameof(name));
}

/// <summary>
/// Allows one sync at a time in the process, whichever client it belongs to. Datasync's own sync lock is process-wide
/// already (C23), and had a race between release and acquire until 10.2.0, so this costs nothing extra. A client stuck on a slow network therefore delays other clients' syncs, though never their local writes.
/// </summary>
internal static class LeewaySyncGate
{
    public static readonly SemaphoreSlim Instance = new(1, 1);
}

/// <summary>
/// The device's side of Leeway. Local writes need no network and never wait for one. See docs/design/escrow-protocol.md.
/// Use one instance per store.
/// </summary>
/// <typeparam name="TContext">The application's store.</typeparam>
/// <param name="createContext">Creates a new store context. Each call to this class uses short-lived ones.</param>
/// <param name="options">Leeway's settings, the same as the context's.</param>
public sealed class LeewayClient<TContext>(Func<TContext> createContext, LeewayClientOptions options) where TContext : LeewayDbContext
{
    private static readonly MethodInfo SnapshotMethod = typeof(LeewayClient<TContext>).GetMethod(nameof(SnapshotAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo RefreshMethod = typeof(LeewayClient<TContext>).GetMethod(nameof(RefreshChangedAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Serializes local writes on this device. It is never held across a network call.</summary>
    private readonly SemaphoreSlim writeGate = new(1, 1);

    /// <summary>Creates a store context, for reading. Saving a merged entity through it is refused.</summary>
    /// <returns>A new context, which the caller disposes.</returns>
    public TContext CreateContext() => createContext();

    /// <summary>Creates the local database if needed.</summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using TContext context = createContext();
        await context.Database.EnsureCreatedAsync(cancellationToken);
        if (!await context.LeewayDevice.AnyAsync(cancellationToken))
        {
            context.LeewayDevice.Add(new LeewayDeviceRow());
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
        }
    }

    /// <summary>Reads an entity as this device sees it, including its own unacknowledged changes.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="id">The entity ID.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The entity.</returns>
    public async Task<T> ReadAsync<T>(string id, CancellationToken cancellationToken = default) where T : OfflineEntity
    {
        await using TContext context = createContext();
        return await context.Set<T>().AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
    }

    /// <summary>Changes an entity's fields, as one operation. Needs no network. If the entity is unknown here, this creates it.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="id">The entity ID.</param>
    /// <param name="edit">The changes.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The operation's ID.</returns>
    public Task<string> EditAsync<T>(string id, Action<EntityEdit<T>> edit, CancellationToken cancellationToken = default) where T : OfflineEntity, new()
        => WriteAsync(async (context, tx) =>
        {
            T row = await context.Set<T>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? AddLocal<T>(context, id);
            await ReprojectAsync(context, row, cancellationToken);

            string operationId = Guid.NewGuid().ToString("N");
            (LeewayDeviceRow device, HybridTimestamp stamp) = await TickAsync(context, cancellationToken);
            EntityEdit<T> changes = new(MergeDocument.FromJson(row.LeewayState), options.DeviceId, stamp, operationId);
            edit(changes);

            await RecordAsync(context, device, stamp, operationId, OperationKind.Merge, typeof(T).Name, id, changes.Delta, null, 0, cancellationToken);
            await ReprojectAsync(context, row, cancellationToken);
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
            return operationId;
        }, cancellationToken);

    /// <summary>
    /// Deletes an entity, here at once and on the server on the next sync. Delete wins over a concurrent edit,
    /// and nothing restores it. Any ledger stays, so spends made offline are still recorded.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="id">The entity ID.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The operation's ID.</returns>
    public Task<string> DeleteAsync<T>(string id, CancellationToken cancellationToken = default) where T : OfflineEntity
        => WriteAsync(async (context, tx) =>
        {
            string operationId = Guid.NewGuid().ToString("N");
            (LeewayDeviceRow device, HybridTimestamp stamp) = await TickAsync(context, cancellationToken);
            await RecordAsync(context, device, stamp, operationId, OperationKind.Merge, typeof(T).Name, id, MergeSchema.DeleteDelta(stamp), null, 0, cancellationToken);
            if (await context.Set<T>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) is { } row)
            {
                context.Set<T>().Remove(row);
            }

            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
            return operationId;
        }, cancellationToken);

    /// <summary>Lists operations the server clearly refused. They are retried on every push until discarded.</summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The refused operations.</returns>
    public async Task<IReadOnlyList<RefusedOperation>> RefusedOperationsAsync(CancellationToken cancellationToken = default)
    {
        await using TContext context = createContext();
        List<CommunityToolkit.Datasync.Client.Offline.DatasyncOperation> failed = await context.DatasyncOperationsQueue.AsNoTracking()
            .Where(q => q.EntityType == typeof(LeewayOperationRow).FullName && q.State == CommunityToolkit.Datasync.Client.Offline.OperationState.Failed)
            .ToListAsync(cancellationToken);

        List<RefusedOperation> refused = [];
        foreach (CommunityToolkit.Datasync.Client.Offline.DatasyncOperation row in failed.Where(q => IsClearRefusal(q.HttpStatusCode)))
        {
            LeewayOutboxRow? outbox = await context.LeewayOutbox.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ItemId, cancellationToken);
            if (outbox is not null)
            {
                Operation op = Operation.FromJson(outbox.Payload);
                refused.Add(new RefusedOperation(op.Id, op.Kind, op.EntityType, op.EntityId, row.HttpStatusCode!.Value));
            }
        }

        return refused;
    }

    /// <summary>
    /// Discards a refused operation, and undoes its effect on the wallet. A discarded edit stays in the row's local view
    /// until the server next changes that row, because a merged state cannot be un-merged.
    /// </summary>
    /// <param name="operationId">The operation's ID.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">It was not clearly refused. It may have been applied, so it must not be discarded.</exception>
    public Task DiscardRefusedAsync(string operationId, CancellationToken cancellationToken = default)
        => WriteAsync(async (context, tx) =>
        {
            CommunityToolkit.Datasync.Client.Offline.DatasyncOperation? queued = await context.DatasyncOperationsQueue.SingleOrDefaultAsync(q => q.ItemId == operationId, cancellationToken);
            if (queued is null || queued.State != CommunityToolkit.Datasync.Client.Offline.OperationState.Failed || !IsClearRefusal(queued.HttpStatusCode))
            {
                throw new InvalidOperationException($"Operation {operationId} was not clearly refused. It may have been applied, so it cannot be discarded.");
            }

            LeewayOutboxRow outbox = await context.LeewayOutbox.SingleAsync(x => x.Id == operationId, cancellationToken);
            Operation op = Operation.FromJson(outbox.Payload);
            if (op.Resource is not null && op.Kind is OperationKind.Spend or OperationKind.ReturnLease)
            {
                LeewayWalletRow wallet = await context.LeewayWallets.SingleAsync(w => w.Resource == op.Resource, cancellationToken);
                wallet.Spent -= op.Kind == OperationKind.Spend ? op.Amount : 0;
                wallet.Returned -= op.Kind == OperationKind.ReturnLease ? op.Amount : 0;
            }

            outbox.State = OutboxState.Discarded;
            context.DatasyncOperationsQueue.Remove(queued);
            if (await context.LeewayOperations.SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken) is { } row)
            {
                context.LeewayOperations.Remove(row);
            }

            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
            if (context.OfflineEntityTypes.FirstOrDefault(t => t.Name == op.EntityType) is { } type
                && await context.FindAsync(type, [op.EntityId], cancellationToken) is OfflineEntity entity)
            {
                await ReprojectAsync(context, entity, cancellationToken);
                await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
            }

            return true;
        }, cancellationToken);

    /// <summary>
    /// Removes old acknowledged operations from this device's records. Unacknowledged ones, those a pulled row does not
    /// include yet, and the latest few always stay. After this, the outcome of a removed operation can no longer be read.
    /// </summary>
    /// <param name="keepLatest">How many acknowledged operations to keep, so that recent outcomes can be read.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>How many were removed.</returns>
    public Task<int> PruneAsync(int keepLatest = 100, CancellationToken cancellationToken = default)
        => WriteAsync(async (context, tx) =>
        {
            List<LeewayOutboxRow> finished = await context.LeewayOutbox
                .Where(x => x.State == OutboxState.Acknowledged || x.State == OutboxState.Discarded)
                .OrderByDescending(x => x.Sequence)
                .Skip(keepLatest)
                .ToListAsync(cancellationToken);

            int removed = 0;
            foreach (LeewayOutboxRow row in finished)
            {
                Type? type = context.OfflineEntityTypes.FirstOrDefault(t => t.Name == row.EntityType);
                long stamp = type is not null && await context.FindAsync(type, [row.EntityId], cancellationToken) is OfflineEntity entity
                    ? entity.UpdatedAt?.ToUnixTimeMilliseconds() ?? 0
                    : long.MaxValue;
                if (row.EntityStamp > stamp)
                {
                    continue;
                }

                context.LeewayOutbox.Remove(row);
                if (await context.LeewayOperations.SingleOrDefaultAsync(x => x.Id == row.Id, cancellationToken) is { } copy)
                {
                    context.LeewayOperations.Remove(copy);
                }

                removed++;
            }

            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
            return removed;
        }, cancellationToken);

    /// <summary>Spends from an escrowed field. Within the lease, it is confirmed with no signal. Beyond it, it is provisional.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="id">The entity ID.</param>
    /// <param name="field">The escrowed field.</param>
    /// <param name="amount">The amount.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>Whether it is confirmed or provisional.</returns>
    public Task<SpendResult> TrySpendAsync<T>(string id, Expression<Func<T, object?>> field, long amount, CancellationToken cancellationToken = default) where T : OfflineEntity
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        string resource = ResourceOf(id, field);
        return WriteAsync(async (context, tx) =>
        {
            long granted = await GrantedAsync(context, resource, cancellationToken);
            await EnsureWalletAsync(context, resource, cancellationToken);

            // One conditional statement, so that nothing can overdraw the lease, even from another context.
            int updated = await context.LeewayWallets
                .Where(w => w.Resource == resource && w.Spent + w.Returned + amount <= granted)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Spent, w => w.Spent + amount), cancellationToken);

            SpendStatus status = updated == 1 ? SpendStatus.Confirmed : SpendStatus.Provisional;
            OperationKind kind = status == SpendStatus.Confirmed ? OperationKind.Spend : OperationKind.ProvisionalSpend;

            string operationId = Guid.NewGuid().ToString("N");
            (LeewayDeviceRow device, HybridTimestamp stamp) = await TickAsync(context, cancellationToken);
            await RecordAsync(context, device, stamp, operationId, kind, typeof(T).Name, id, null, resource, amount, cancellationToken);

            if (await context.Set<T>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) is { } row)
            {
                await ReprojectAsync(context, row, cancellationToken);
            }

            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
            return new SpendResult(status, operationId);
        }, cancellationToken);
    }

    /// <summary>Asks for a lease on an escrowed field. It arrives on the next sync.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="id">The entity ID.</param>
    /// <param name="field">The escrowed field.</param>
    /// <param name="amount">The amount, or zero for the default lease.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The operation's ID.</returns>
    public Task<string> RequestLeaseAsync<T>(string id, Expression<Func<T, object?>> field, long amount = 0, CancellationToken cancellationToken = default) where T : OfflineEntity
    {
        string resource = ResourceOf(id, field);
        return WriteAsync(async (context, tx) =>
        {
            string operationId = Guid.NewGuid().ToString("N");
            (LeewayDeviceRow device, HybridTimestamp stamp) = await TickAsync(context, cancellationToken);
            await RecordAsync(context, device, stamp, operationId, OperationKind.LeaseRequest, typeof(T).Name, id, null, resource, amount, cancellationToken);
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
            return operationId;
        }, cancellationToken);
    }

    /// <summary>Gives unused lease back. It stops being spendable here at once.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="id">The entity ID.</param>
    /// <param name="field">The escrowed field.</param>
    /// <param name="amount">The amount wanted.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>What was really given back, which is at most what is available.</returns>
    public Task<long> ReturnLeaseAsync<T>(string id, Expression<Func<T, object?>> field, long amount, CancellationToken cancellationToken = default) where T : OfflineEntity
    {
        string resource = ResourceOf(id, field);
        return WriteAsync(async (context, tx) =>
        {
            long granted = await GrantedAsync(context, resource, cancellationToken);
            LeewayWalletRow wallet = await EnsureWalletAsync(context, resource, cancellationToken);
            (Wallet after, long returned) = new Wallet(granted, wallet.Spent, wallet.Returned).Return(amount);
            if (returned == 0)
            {
                return 0L;
            }

            wallet.Returned = after.Returned;
            string operationId = Guid.NewGuid().ToString("N");
            (LeewayDeviceRow device, HybridTimestamp stamp) = await TickAsync(context, cancellationToken);
            await RecordAsync(context, device, stamp, operationId, OperationKind.ReturnLease, typeof(T).Name, id, null, resource, returned, cancellationToken);
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
            return returned;
        }, cancellationToken);
    }

    /// <summary>Reads this device's position in an escrowed field.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="id">The entity ID.</param>
    /// <param name="field">The escrowed field.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The wallet.</returns>
    public async Task<Wallet> WalletAsync<T>(string id, Expression<Func<T, object?>> field, CancellationToken cancellationToken = default) where T : OfflineEntity
    {
        string resource = ResourceOf(id, field);
        await using TContext context = createContext();
        long granted = await GrantedAsync(context, resource, cancellationToken);
        LeewayWalletRow? wallet = await context.LeewayWallets.AsNoTracking().SingleOrDefaultAsync(w => w.Resource == resource, cancellationToken);
        return new Wallet(granted, wallet?.Spent ?? 0, wallet?.Returned ?? 0);
    }

    /// <summary>Reads what the server did with an operation.</summary>
    /// <param name="operationId">The operation's ID.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The outcome, or null if the server has not answered yet.</returns>
    public async Task<OperationOutcome?> OutcomeOfAsync(string operationId, CancellationToken cancellationToken = default)
    {
        await using TContext context = createContext();
        LeewayOutboxRow? row = await context.LeewayOutbox.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
        return row?.OutcomeStatus is null ? null : new OperationOutcome(Enum.Parse<OutcomeStatus>(row.OutcomeStatus), row.OutcomeAmount);
    }

    /// <summary>Counts operations the server has not acknowledged.</summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The count.</returns>
    public async Task<int> PendingCountAsync(CancellationToken cancellationToken = default)
    {
        await using TContext context = createContext();
        return await context.LeewayOutbox.CountAsync(x => x.State != OutboxState.Acknowledged && x.State != OutboxState.Discarded, cancellationToken);
    }

    /// <summary>Sends operations, then pulls. Never throws for network trouble: it reports it.</summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>How it went.</returns>
    public async Task<SyncReport> SyncAsync(CancellationToken cancellationToken = default)
    {
        await LeewaySyncGate.Instance.WaitAsync(cancellationToken);
        try
        {
            SyncReport push = await PushCoreAsync(cancellationToken);
            if (push.State == SyncState.Offline)
            {
                return push;
            }

            bool pulled = await PullCoreAsync(cancellationToken);
            return pulled ? push : new SyncReport(SyncState.Offline, push.RejectedOperations);
        }
        finally
        {
            LeewaySyncGate.Instance.Release();
        }
    }

    /// <summary>Sends operations only.</summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>How it went.</returns>
    public async Task<SyncReport> PushAsync(CancellationToken cancellationToken = default)
    {
        await LeewaySyncGate.Instance.WaitAsync(cancellationToken);
        try
        {
            return await PushCoreAsync(cancellationToken);
        }
        finally
        {
            LeewaySyncGate.Instance.Release();
        }
    }

    /// <summary>Pulls only.</summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>How it went.</returns>
    public async Task<SyncReport> PullAsync(CancellationToken cancellationToken = default)
    {
        await LeewaySyncGate.Instance.WaitAsync(cancellationToken);
        try
        {
            return new SyncReport(await PullCoreAsync(cancellationToken) ? SyncState.Synced : SyncState.Offline);
        }
        finally
        {
            LeewaySyncGate.Instance.Release();
        }
    }

    /// <summary>A 4xx that is not a conflict or a timeout, so the server cannot have applied it, and a retry cannot help.</summary>
    private static bool IsClearRefusal(int? status) => status is >= 400 and < 500 and not (408 or 409 or 412 or 425 or 429);

    /// <summary>
    /// Sends one operation at a time, oldest first, because Datasync's queue has no order of its own. It stops at the
    /// first with no answer, so that a device's spends compete first come, first served. See docs/design/hardening.md.
    /// </summary>
    private async Task<SyncReport> PushCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            bool more = await FlushNextAsync(cancellationToken);
            SyncReport report = await PushOnceAsync(cancellationToken);
            if (report.State == SyncState.Offline || !more)
            {
                return report;
            }
        }
    }

    private async Task<SyncReport> PushOnceAsync(CancellationToken cancellationToken)
    {
        bool unreachable = false;
        await using (TContext context = createContext())
        {
            try
            {
                await context.PushAsync([typeof(LeewayOperationRow)], new PushOptions(), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException && !cancellationToken.IsCancellationRequested)
            {
                unreachable = true;
            }
        }

        await RecordOutcomesAsync(cancellationToken);

        // A guard. Up to Datasync 10.1.2, a push that timed out reported success with the operation still queued (probe C24).
        await using TContext check = createContext();
        List<CommunityToolkit.Datasync.Client.Offline.OperationState> queue = await check.DatasyncOperationsQueue
            .Where(q => q.EntityType == typeof(LeewayOperationRow).FullName)
            .Select(q => q.State)
            .ToListAsync(cancellationToken);
        int rejected = queue.Count(s => s == CommunityToolkit.Datasync.Client.Offline.OperationState.Failed);

        if (unreachable || queue.Count > rejected)
        {
            return new SyncReport(SyncState.Offline, rejected);
        }

        return rejected > 0 ? new SyncReport(SyncState.Failed, rejected) : new SyncReport(SyncState.Synced);
    }

    /// <summary>
    /// Copies the oldest operation into the Datasync queue, unless an earlier one is still waiting for an answer. This is
    /// the only queued write, and it may wait for the process-wide sync lock (probe C23), so it holds no gate that a spend needs.
    /// </summary>
    /// <returns>Whether there may be more to send after this push.</returns>
    private async Task<bool> FlushNextAsync(CancellationToken cancellationToken)
    {
        await using TContext context = createContext();
        bool waiting = await context.DatasyncOperationsQueue.AnyAsync(
            q => q.EntityType == typeof(LeewayOperationRow).FullName && q.State == CommunityToolkit.Datasync.Client.Offline.OperationState.Pending,
            cancellationToken);
        LeewayOutboxRow? next = await context.LeewayOutbox.Where(x => x.State == OutboxState.Pending).OrderBy(x => x.Sequence).FirstOrDefaultAsync(cancellationToken);
        if (waiting)
        {
            return next is not null;
        }

        if (next is null)
        {
            return false;
        }

        context.LeewayOperations.Add(new LeewayOperationRow { Id = next.Id, Device = options.DeviceId, Payload = next.Payload });
        next.State = OutboxState.Queued;
        await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: true, cancellationToken);
        return true;
    }

    private async Task RecordOutcomesAsync(CancellationToken cancellationToken)
    {
        await using TContext context = createContext();
        List<LeewayOperationRow> answered = await context.LeewayOperations.AsNoTracking().Where(x => x.OutcomeStatus != null).ToListAsync(cancellationToken);
        if (answered.Count == 0)
        {
            return;
        }

        Dictionary<string, LeewayOperationRow> byId = answered.ToDictionary(x => x.Id);
        string[] ids = [.. byId.Keys];
        List<LeewayOutboxRow> rows = await context.LeewayOutbox.Where(x => x.State != OutboxState.Acknowledged && ids.Contains(x.Id)).ToListAsync(cancellationToken);
        foreach (LeewayOutboxRow row in rows)
        {
            LeewayOperationRow answer = byId[row.Id];
            row.State = OutboxState.Acknowledged;
            row.OutcomeStatus = answer.OutcomeStatus;
            row.OutcomeAmount = answer.OutcomeAmount;
            row.EntityStamp = answer.EntityStamp;
        }

        await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
    }

    private async Task<bool> PullCoreAsync(CancellationToken cancellationToken)
    {
        Type[] entityTypes;
        Dictionary<Type, Dictionary<string, string?>> before = [];
        await using (TContext context = createContext())
        {
            entityTypes = [.. context.OfflineEntityTypes];
            foreach (Type type in entityTypes)
            {
                before[type] = await (Task<Dictionary<string, string?>>)SnapshotMethod.MakeGenericMethod(type).Invoke(this, [context, cancellationToken])!;
            }
        }

        await using (TContext context = createContext())
        {
            try
            {
                PullResult result = await context.PullAsync([.. entityTypes, typeof(LeewayLeaseRow)], new PullOptions(), cancellationToken);
                if (!result.IsSuccessful)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        // Pull overwrote the changed rows, so put this device's unsettled changes back on top, and learn the server's stamps.
        await this.writeGate.WaitAsync(cancellationToken);
        try
        {
            await using TContext context = createContext();
            HybridTimestamp highest = default;
            foreach (Type type in entityTypes)
            {
                HybridTimestamp seen = await (Task<HybridTimestamp>)RefreshMethod.MakeGenericMethod(type).Invoke(this, [context, before[type], cancellationToken])!;
                if (seen.CompareTo(highest) > 0)
                {
                    highest = seen;
                }
            }

            LeewayDeviceRow device = await context.LeewayDevice.SingleAsync(cancellationToken);
            HybridTimestamp observed = new(device.ObservedPhysical, device.ObservedLogical, device.ObservedReplica);
            if (highest.CompareTo(observed) > 0)
            {
                device.ObservedPhysical = highest.Physical;
                device.ObservedLogical = highest.Logical;
                device.ObservedReplica = highest.Replica;
                HybridClock clock = ClockOf(device);
                clock.Observe(highest);
                device.ClockPhysical = clock.Last.Physical;
                device.ClockLogical = clock.Last.Logical;
            }

            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
        }
        finally
        {
            this.writeGate.Release();
        }

        return true;
    }

    private async Task<Dictionary<string, string?>> SnapshotAsync<T>(TContext context, CancellationToken cancellationToken) where T : OfflineEntity
        => await context.Set<T>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Version, cancellationToken);

    private async Task<HybridTimestamp> RefreshChangedAsync<T>(TContext context, Dictionary<string, string?> before, CancellationToken cancellationToken) where T : OfflineEntity
    {
        HybridTimestamp highest = default;
        foreach (T row in await context.Set<T>().ToListAsync(cancellationToken))
        {
            if (before.TryGetValue(row.Id, out string? version) && version == row.Version)
            {
                continue;
            }

            // Before merging local changes, this is exactly what the server sent.
            HybridTimestamp seen = MergeDocument.FromJson(row.LeewayState).HighestStamp();
            if (seen.CompareTo(highest) > 0)
            {
                highest = seen;
            }

            await ReprojectAsync(context, row, cancellationToken);
        }

        return highest;
    }

    /// <summary>
    /// Recomputes a row's local view from what was pulled plus every unsettled operation. Merging is idempotent and the
    /// escrow figure is computed fresh, so this is safe to repeat, and safe if it races a pull. An acknowledged operation
    /// stays unsettled until the row includes it. Otherwise the next counter add would start from a stale total.
    /// The simulator found that. See docs/design/merge-semantics.md.
    /// </summary>
    private static async Task ReprojectAsync(TContext context, OfflineEntity row, CancellationToken cancellationToken)
    {
        string entityType = row.GetType().Name;
        long rowStamp = row.UpdatedAt?.ToUnixTimeMilliseconds() ?? 0;

        List<LeewayOutboxRow> saved = await context.LeewayOutbox.AsNoTracking()
            .Where(x => x.EntityType == entityType && x.EntityId == row.Id && x.State != OutboxState.Discarded && (x.State != OutboxState.Acknowledged || x.EntityStamp > rowStamp))
            .ToListAsync(cancellationToken);
        IEnumerable<LeewayOutboxRow> added = context.ChangeTracker.Entries<LeewayOutboxRow>()
            .Where(e => e.State == EntityState.Added && e.Entity.EntityType == entityType && e.Entity.EntityId == row.Id)
            .Select(e => e.Entity);

        MergeDocument document = MergeDocument.FromJson(row.LeewayState);
        Dictionary<string, long> spends = [];
        foreach (Operation op in saved.Concat(added).OrderBy(x => x.Sequence).Select(x => Operation.FromJson(x.Payload)))
        {
            if (op.Delta is not null)
            {
                document = document.Merge(op.Delta);
            }
            else if (op is { Kind: OperationKind.Spend, Resource: not null })
            {
                spends[op.Resource] = spends.GetValueOrDefault(op.Resource) + op.Amount;
            }
        }

        MergeSchema schema = MergeSchema.For(row.GetType());
        row.LeewayState = document.ToJson();
        schema.Project(document, row);
        foreach (MergeField field in schema.EscrowFields)
        {
            string resource = MergeSchema.EscrowResource(entityType, row.Id, field.Name);
            MergeSchema.ProjectEscrow(row, field, MergeSchema.EscrowRemaining(document, field.Name) - spends.GetValueOrDefault(resource));
        }
    }

    private static T AddLocal<T>(TContext context, string id) where T : OfflineEntity, new()
    {
        T row = new() { Id = id };
        context.Set<T>().Add(row);
        return row;
    }

    private async Task<TResult> WriteAsync<TResult>(Func<TContext, IDbContextTransaction, Task<TResult>> work, CancellationToken cancellationToken)
    {
        await this.writeGate.WaitAsync(cancellationToken);
        try
        {
            await using TContext context = createContext();
            await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            TResult result = await work(context, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            this.writeGate.Release();
        }
    }

    private HybridClock ClockOf(LeewayDeviceRow device)
        => new(options.DeviceId, () => options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(), new HybridTimestamp(device.ClockPhysical, device.ClockLogical, options.DeviceId));

    private async Task<(LeewayDeviceRow Device, HybridTimestamp Stamp)> TickAsync(TContext context, CancellationToken cancellationToken)
    {
        LeewayDeviceRow device = await context.LeewayDevice.SingleAsync(cancellationToken);
        HybridTimestamp stamp = ClockOf(device).Now();
        device.ClockPhysical = stamp.Physical;
        device.ClockLogical = stamp.Logical;
        return (device, stamp);
    }

    private async Task RecordAsync(TContext context, LeewayDeviceRow device, HybridTimestamp stamp, string operationId, OperationKind kind, string entityType, string entityId, MergeDocument? delta, string? resource, long amount, CancellationToken cancellationToken)
    {
        long sequence = ++device.Sequence;
        long? oldest = await context.LeewayOutbox.Where(x => x.State != OutboxState.Acknowledged && x.State != OutboxState.Discarded).MinAsync(x => (long?)x.Sequence, cancellationToken);
        Operation op = new(
            operationId, options.DeviceId, sequence, stamp,
            new HybridTimestamp(device.ObservedPhysical, device.ObservedLogical, device.ObservedReplica),
            kind, entityType, entityId, delta, resource, amount, oldest ?? sequence);
        context.LeewayOutbox.Add(new LeewayOutboxRow { Id = operationId, Sequence = sequence, EntityType = entityType, EntityId = entityId, Payload = op.ToJson() });
    }

    private static async Task<long> GrantedAsync(TContext context, string resource, CancellationToken cancellationToken)
        => await context.LeewayLeases.AsNoTracking().Where(x => x.Resource == resource).Select(x => x.Granted).FirstOrDefaultAsync(cancellationToken);

    private static async Task<LeewayWalletRow> EnsureWalletAsync(TContext context, string resource, CancellationToken cancellationToken)
    {
        LeewayWalletRow? wallet = await context.LeewayWallets.SingleOrDefaultAsync(w => w.Resource == resource, cancellationToken);
        if (wallet is null)
        {
            wallet = new LeewayWalletRow { Resource = resource };
            context.LeewayWallets.Add(wallet);
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false, cancellationToken);
        }

        return wallet;
    }

    private static string ResourceOf<T>(string id, Expression<Func<T, object?>> field) where T : OfflineEntity
    {
        string name = MergeSchema.FieldName(field);
        if (!MergeSchema.For(typeof(T)).Fields.TryGetValue(name, out MergeField? target) || target.Kind != FieldKind.Escrow)
        {
            throw new ArgumentException($"{typeof(T).Name}.{name} is not an [Escrow] field.", nameof(field));
        }

        return MergeSchema.EscrowResource(typeof(T).Name, id, name);
    }
}
