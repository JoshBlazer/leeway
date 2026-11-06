using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Datasync.Server;
using Leeway.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Leeway.Datasync.Server;

/// <summary>Leeway's server settings.</summary>
public sealed class LeewayServerOptions
{
    private readonly Dictionary<string, Type> typesByName = [];
    private readonly Dictionary<Type, string> namesByType = [];

    /// <summary>The time source for stamps.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>How far ahead of the server a device's stamp may be.</summary>
    public long MaxDriftMs { get; set; } = StampAuthority.DefaultMaxDriftMs;

    /// <summary>Chooses a resource's allocation policy. By default, a fixed lease of the size in the field's [Escrow] attribute.</summary>
    public Func<MergeField, IAllocationPolicy> AllocationPolicy { get; set; } = field => new FixedLease(field.Lease);

    /// <summary>
    /// Gets the calling device's identity from the request, or null if it is not authenticated. Leeway takes an
    /// identity from the application, and does not issue or check credentials itself.
    /// </summary>
    public Func<HttpContext, string?>? AuthenticateDevice { get; set; }

    /// <summary>Explicitly allows any client to claim to be any device. For samples and tests, never production.</summary>
    public bool AllowUnauthenticatedDevices { get; set; }

    /// <summary>Registers an entity type.</summary>
    /// <typeparam name="T">The server entity class.</typeparam>
    /// <param name="name">The name operations use for it, which is the client class's name. By default, this class's name.</param>
    /// <returns>These options.</returns>
    public LeewayServerOptions Entity<T>(string? name = null) where T : LeewayEntity, new()
    {
        string entityName = name ?? typeof(T).Name;
        this.typesByName[entityName] = typeof(T);
        this.namesByType[typeof(T)] = entityName;
        return this;
    }

    internal Type? TypeNamed(string name) => this.typesByName.GetValueOrDefault(name);

    internal string NameOf(Type type) => this.namesByType.TryGetValue(type, out string? name)
        ? name
        : throw new InvalidOperationException($"{type.Name} is not registered. Call options.Entity<{type.Name}>().");
}

/// <summary>Serializes writes, so that each operation's check, apply, and stamp are one step (ADR-0013). In-process only.</summary>
public sealed class LeewayWriteGate
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);

    /// <summary>Whether this process has checked the database's collation.</summary>
    internal bool CollationChecked { get; set; }
}

/// <summary>Service registration.</summary>
public static class LeewayServiceCollectionExtensions
{
    /// <summary>Adds Leeway and Datasync services.</summary>
    /// <typeparam name="TContext">The application's DbContext.</typeparam>
    /// <param name="services">The services.</param>
    /// <param name="configure">Configures Leeway.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddLeewayServer<TContext>(this IServiceCollection services, Action<LeewayServerOptions> configure) where TContext : DbContext
    {
        LeewayServerOptions options = new();
        configure(options);
        if (options.AuthenticateDevice is null && !options.AllowUnauthenticatedDevices)
        {
            throw new InvalidOperationException(
                "Leeway needs a decision about device authentication. Set AuthenticateDevice, or set AllowUnauthenticatedDevices if this is a sample or test.");
        }

        services.AddHttpContextAccessor();
        services.AddSingleton(options);
        services.AddSingleton<LeewayWriteGate>();
        services.AddScoped<LeewayAuthority<TContext>>();
        services.AddDatasyncServices();
        return services;
    }
}

/// <summary>
/// The server's authority: applies each operation in one transaction with recording it, using the same pure rules
/// the simulator tests. See docs/design/escrow-protocol.md and ADR-0012.
/// </summary>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
public sealed class LeewayAuthority<TContext>(TContext context, LeewayServerOptions options, LeewayWriteGate gate, IHttpContextAccessor http) where TContext : DbContext
{
    private long Now => options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Whether any client may claim to be any device.</summary>
    internal bool IsOpen => options.AuthenticateDevice is null;

    /// <summary>The authenticated device, or null if there is none.</summary>
    internal string? CurrentDevice => http.HttpContext is { } current ? options.AuthenticateDevice?.Invoke(current) : null;

    /// <summary>Creates an entity, along with a ledger for each escrowed field.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="entity">The entity, with its initial values. For an escrowed field, the quantity above the minimum becomes the total.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task.</returns>
    public async Task CreateEntityAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default) where TEntity : LeewayEntity
    {
        await InTransactionAsync(async () =>
        {
            string name = options.NameOf(entity.GetType());
            if (string.IsNullOrEmpty(entity.Id))
            {
                entity.Id = Guid.NewGuid().ToString("N");
            }

            LeewayAuthorityRow authority = await LoadAuthorityAsync(cancellationToken);
            long stamp = NextStamp(authority);
            HybridTimestamp serverStamp = new(stamp, 0, "$server");
            MergeSchema schema = MergeSchema.For(entity.GetType());
            MergeDocument document = schema.Seed(entity, serverStamp);

            foreach (MergeField field in schema.EscrowFields)
            {
                long total = Convert.ToInt64(field.Get(entity)) - field.Min;
                if (total < 0)
                {
                    throw new ArgumentException($"{field.Name} starts below its minimum.", nameof(entity));
                }

                string resource = MergeSchema.EscrowResource(name, entity.Id, field.Name);
                context.Add(new LeewayEscrowRow { Resource = resource, EntityType = name, EntityId = entity.Id, Field = field.Name, Ledger = EscrowLedger.Create(resource, total).ToJson() });
                document = MergeSchema.WithEscrowRemaining(document, field.Name, total, serverStamp);
            }

            Stamp(entity, document, stamp);
            context.Add(entity);
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Reads a resource's ledger.</summary>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The ledger, or null if the resource does not exist.</returns>
    public async Task<EscrowLedger?> GetLedgerAsync(string resource, CancellationToken cancellationToken = default)
    {
        LeewayEscrowRow? row = await context.Set<LeewayEscrowRow>().AsNoTracking().SingleOrDefaultAsync(x => x.Resource == resource, cancellationToken);
        return row is null ? null : EscrowLedger.FromJson(row.Ledger);
    }

    /// <summary>
    /// An admin takes back what a device holds, returning it to the pool. If the device was only offline, its spends
    /// cannot now be funded, so the limit can be exceeded. They are recorded as overdrafts (ADR-0005).
    /// </summary>
    /// <param name="resource">The resource name.</param>
    /// <param name="device">The device.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task.</returns>
    public async Task WriteOffAsync(string resource, string device, CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async () =>
        {
            LeewayEscrowRow row = await context.Set<LeewayEscrowRow>().SingleAsync(x => x.Resource == resource, cancellationToken);
            row.Ledger = EscrowLedger.FromJson(row.Ledger).WriteOff(device).ToJson();
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes operations that their devices know were acknowledged. Those devices will never send them again, so no
    /// replay can be applied twice. A device that has gone quiet keeps its operations until it returns.
    /// </summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>How many were deleted.</returns>
    public async Task<int> PruneOperationsAsync(CancellationToken cancellationToken = default)
    {
        int pruned = 0;
        await InTransactionAsync(async () =>
        {
            foreach (LeewayDeviceProgressRow progress in await context.Set<LeewayDeviceProgressRow>().AsNoTracking().ToListAsync(cancellationToken))
            {
                pruned += await context.Set<LeewayOperationRow>()
                    .Where(x => x.Device == progress.Device && x.Sequence < progress.SettledBelow)
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }, cancellationToken);
        return pruned;
    }

    /// <summary>Applies an incoming operation and records it, as one step. A duplicate gets a 409 with the stored row.</summary>
    /// <param name="row">The incoming row.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task.</returns>
    internal async Task ApplyAsync(LeewayOperationRow row, CancellationToken cancellationToken)
    {
        await InTransactionAsync(async () =>
        {
            LeewayOperationRow? existing = await context.Set<LeewayOperationRow>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id, cancellationToken);
            if (existing is not null)
            {
                throw new HttpException(StatusCodes.Status409Conflict) { Payload = existing };
            }

            Operation op = Parse(row);
            LeewayAuthorityRow authority = await LoadAuthorityAsync(cancellationToken);
            long stamp = NextStamp(authority);

            (OperationOutcome outcome, long entityStamp) = op.Kind == OperationKind.Merge
                ? await ApplyMergeAsync(op, authority, stamp, cancellationToken)
                : await ApplyEscrowAsync(op, stamp, cancellationToken);

            await RecordProgressAsync(op, cancellationToken);
            row.Sequence = op.Sequence;
            row.OutcomeStatus = outcome.Status.ToString();
            row.OutcomeAmount = outcome.Amount;
            row.EntityStamp = entityStamp;
            row.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(stamp);
            row.Version = Guid.NewGuid().ToByteArray();
            context.Add(row);
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    private async Task RecordProgressAsync(Operation op, CancellationToken cancellationToken)
    {
        LeewayDeviceProgressRow? progress = await context.Set<LeewayDeviceProgressRow>().SingleOrDefaultAsync(x => x.Device == op.Device, cancellationToken);
        if (progress is null)
        {
            context.Add(new LeewayDeviceProgressRow { Device = op.Device, SettledBelow = op.SettledBelow });
        }
        else if (op.SettledBelow > progress.SettledBelow)
        {
            progress.SettledBelow = op.SettledBelow;
        }
    }

    private static Operation Parse(LeewayOperationRow row)
    {
        Operation op;
        try
        {
            op = Operation.FromJson(row.Payload);
        }
        catch (JsonException ex)
        {
            throw new HttpException(StatusCodes.Status400BadRequest, "The operation is not valid.", ex);
        }

        if (op.Id != row.Id || op.Device != row.Device)
        {
            throw new HttpException(StatusCodes.Status400BadRequest, "The operation does not match its row.");
        }

        return op;
    }

    private async Task<(OperationOutcome Outcome, long EntityStamp)> ApplyMergeAsync(Operation op, LeewayAuthorityRow authority, long stamp, CancellationToken cancellationToken)
    {
        if (op.Delta is null || options.TypeNamed(op.EntityType) is not { } type)
        {
            throw new HttpException(StatusCodes.Status400BadRequest, $"Unknown entity type {op.EntityType}.");
        }

        LeewayEntity? entity = (LeewayEntity?)await context.FindAsync(type, [op.EntityId], cancellationToken);
        if (entity is null)
        {
            entity = (LeewayEntity)Activator.CreateInstance(type)!;
            entity.Id = op.EntityId;
            context.Add(entity);
        }

        StampAuthority stamps = authority.Stamps is null ? StampAuthority.Create(options.MaxDriftMs) : StampAuthority.FromJson(authority.Stamps);
        (HybridTimestamp canonical, StampAuthority next) = stamps.Admit(op, Now);
        authority.Stamps = next.ToJson();

        MergeDocument document = MergeDocument.FromJson(entity.LeewayState).Merge(op.Delta.WithRegisterStamps(canonical));
        MergeSchema.For(type).Project(document, entity);
        entity.Deleted = MergeSchema.IsDeleted(document);
        Stamp(entity, document, stamp);
        return (new OperationOutcome(OutcomeStatus.Merged), stamp);
    }

    private async Task<(OperationOutcome Outcome, long EntityStamp)> ApplyEscrowAsync(Operation op, long stamp, CancellationToken cancellationToken)
    {
        LeewayEscrowRow row = await context.Set<LeewayEscrowRow>().SingleOrDefaultAsync(x => x.Resource == op.Resource, cancellationToken)
            ?? throw new HttpException(StatusCodes.Status400BadRequest, $"Unknown resource {op.Resource}.");
        if (options.TypeNamed(row.EntityType) is not { } type)
        {
            throw new HttpException(StatusCodes.Status400BadRequest, $"Unknown entity type {row.EntityType}.");
        }

        MergeField field = MergeSchema.For(type).Fields[row.Field];
        EscrowLedger before = EscrowLedger.FromJson(row.Ledger);
        (EscrowLedger after, OperationOutcome outcome) = EscrowRules.Apply(before, op, options.AllocationPolicy(field));
        row.Ledger = after.ToJson();

        if (outcome is { Status: OutcomeStatus.LeaseGranted, Amount: > 0 })
        {
            await UpdateLeaseAsync(after, op, stamp, cancellationToken);
        }

        if (after.Consumed == before.Consumed)
        {
            return (outcome, 0);
        }

        LeewayEntity entity = (LeewayEntity)(await context.FindAsync(type, [row.EntityId], cancellationToken))!;
        MergeDocument document = MergeSchema.WithEscrowRemaining(MergeDocument.FromJson(entity.LeewayState), field.Name, after.Remaining, new HybridTimestamp(stamp, 0, "$server"));
        MergeSchema.ProjectEscrow(entity, field, after.Remaining);
        Stamp(entity, document, stamp);
        return (outcome, stamp);
    }

    private async Task UpdateLeaseAsync(EscrowLedger ledger, Operation op, long stamp, CancellationToken cancellationToken)
    {
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{ledger.Resource}|{op.Device}")))[..32];
        LeewayLeaseRow? lease = await context.Set<LeewayLeaseRow>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (lease is null)
        {
            lease = new LeewayLeaseRow { Id = id, Resource = ledger.Resource, Device = op.Device };
            context.Add(lease);
        }

        lease.Granted = ledger.Accounts[op.Device].Granted;
        lease.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(stamp);
        lease.Version = Guid.NewGuid().ToByteArray();
    }

    private static void Stamp(LeewayEntity entity, MergeDocument document, long stamp)
    {
        entity.LeewayState = document.ToJson();
        entity.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(stamp);
        entity.Version = Guid.NewGuid().ToByteArray();
    }

    /// <summary>Strictly increasing stamps, so that no row can share the client's delta token (ADR-0013, probe C19).</summary>
    private long NextStamp(LeewayAuthorityRow authority)
    {
        authority.LastStampMs = Math.Max(Now, authority.LastStampMs + 1);
        return authority.LastStampMs;
    }

    private async Task<LeewayAuthorityRow> LoadAuthorityAsync(CancellationToken cancellationToken)
    {
        LeewayAuthorityRow? authority = await context.Set<LeewayAuthorityRow>().SingleOrDefaultAsync(x => x.Id == LeewayAuthorityRow.SingleId, cancellationToken);
        if (authority is null)
        {
            authority = new LeewayAuthorityRow();
            context.Add(authority);
        }

        return authority;
    }

    /// <summary>
    /// The gate above only covers this process. On a database that has row locks, every write first locks the authority
    /// row, so that writes from all server processes go one at a time. That makes stamp order the same as commit order,
    /// which is what lets a pull's delta token be trusted (ADR-0013). It must come first in the transaction, because at
    /// MySQL's default isolation level, the first plain read fixes what the transaction sees.
    /// SQLite has only one writer anyway, and no row locks. Other databases have not been tested.
    /// </summary>
    private async Task LockAcrossProcessesAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        if (context.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        // Table names come from the EF model, never from a request, so building this text is safe.
        string table = context.Model.FindEntityType(typeof(LeewayAuthorityRow))!.GetTableName()!;
        string lockRow = $"SELECT `Id` FROM `{table}` WHERE `Id` = '{LeewayAuthorityRow.SingleId}' FOR UPDATE";

        // Lock first. An INSERT IGNORE on an existing row takes a shared lock, so two writers doing that and then
        // locking would deadlock. So insert only if the row is missing, which happens once in a database's life.
        if (await ExecuteAsync(transaction, lockRow, cancellationToken) is null)
        {
            await ExecuteAsync(transaction, $"INSERT IGNORE INTO `{table}` (`Id`, `LastStampMs`) VALUES ('{LeewayAuthorityRow.SingleId}', 0)", cancellationToken);
            await ExecuteAsync(transaction, lockRow, cancellationToken);
        }

        // Only after the lock: this is a plain read, and it fixes what the transaction sees.
        await CheckCollationAsync(transaction, cancellationToken);
    }

    private async Task<object?> ExecuteAsync(IDbContextTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        await using (command)
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A case-insensitive collation, MySQL's default, would make gate-a and GATE-A match in the access checks and the
    /// operation table. That is a security problem which would otherwise show no symptoms, so refuse to run on one.
    /// </summary>
    private async Task CheckCollationAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        if (gate.CollationChecked)
        {
            return;
        }

        string table = context.Model.FindEntityType(typeof(LeewayOperationRow))!.GetTableName()!;
        string sql = $"SELECT COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND LOWER(TABLE_NAME) = LOWER('{table}') AND COLUMN_NAME = 'Device'";
        string collation = await ExecuteAsync(transaction, sql, cancellationToken) as string ?? string.Empty;
        if (!collation.EndsWith("_bin", StringComparison.OrdinalIgnoreCase) && !collation.EndsWith("_cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Column {table}.Device has collation '{collation}', which ignores case, so 'gate-a' and 'GATE-A' would be the same device. " +
                "Create the database with a case-sensitive collation, such as: CREATE DATABASE app COLLATE utf8mb4_bin.");
        }

        gate.CollationChecked = true;
    }

    private async Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await LockAcrossProcessesAsync(transaction, cancellationToken);
            await work();
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }
}
