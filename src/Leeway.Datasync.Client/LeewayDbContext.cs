using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Datasync.Client.Offline;
using Microsoft.EntityFrameworkCore;

namespace Leeway.Datasync.Client;

/// <summary>The base class for a client entity with merge semantics. Change it through <see cref="LeewayClient{TContext}"/>.</summary>
public abstract class OfflineEntity
{
    /// <summary>The entity ID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>When the server last changed the row.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The server's version of the row.</summary>
    public string? Version { get; set; }

    /// <summary>Whether the row is deleted.</summary>
    public bool Deleted { get; set; }

    /// <summary>The merge state, as JSON. The other columns are projected from it.</summary>
    public string? LeewayState { get; set; }
}

/// <summary>The client's copy of an operation row. Adding one to the queue is how an operation travels.</summary>
public class LeewayOperationRow
{
    /// <summary>The operation's UUID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>When the server recorded it.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The server's version of the row.</summary>
    public string? Version { get; set; }

    /// <summary>Whether the row is deleted.</summary>
    public bool Deleted { get; set; }

    /// <summary>The device.</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>The operation, as JSON.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>What the server did.</summary>
    public string? OutcomeStatus { get; set; }

    /// <summary>The amount involved in the outcome.</summary>
    public long OutcomeAmount { get; set; }

    /// <summary>The stamp of the entity row that includes this operation, or zero if the row did not change.</summary>
    public long EntityStamp { get; set; }
}

/// <summary>This device's cumulative grant on a resource, as pulled from the server.</summary>
public class LeewayLeaseRow
{
    /// <summary>The row ID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>When the server last changed the row.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The server's version of the row.</summary>
    public string? Version { get; set; }

    /// <summary>Whether the row is deleted.</summary>
    public bool Deleted { get; set; }

    /// <summary>The resource.</summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>The device.</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>The total granted.</summary>
    public long Granted { get; set; }
}

/// <summary>Where an operation has got to.</summary>
public enum OutboxState
{
    /// <summary>Recorded locally. Written with no network or sync lock involved (probe C23).</summary>
    Pending,

    /// <summary>Copied into the Datasync queue.</summary>
    Queued,

    /// <summary>The server has answered.</summary>
    Acknowledged,
}

/// <summary>This device's durable record of its operations. Never synchronized.</summary>
public class LeewayOutboxRow
{
    /// <summary>The operation's UUID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The device's sequence number.</summary>
    public long Sequence { get; set; }

    /// <summary>The entity type name.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The entity ID.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>The operation, as JSON.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Where it has got to.</summary>
    public OutboxState State { get; set; }

    /// <summary>What the server did.</summary>
    public string? OutcomeStatus { get; set; }

    /// <summary>The amount involved in the outcome.</summary>
    public long OutcomeAmount { get; set; }

    /// <summary>The stamp of the entity row that includes this operation.</summary>
    public long EntityStamp { get; set; }
}

/// <summary>What this device has done with a resource. Never synchronized. See docs/design/escrow-protocol.md.</summary>
public class LeewayWalletRow
{
    /// <summary>The resource.</summary>
    [Key]
    public string Resource { get; set; } = string.Empty;

    /// <summary>The total this device has confirmed.</summary>
    public long Spent { get; set; }

    /// <summary>The total this device has returned.</summary>
    public long Returned { get; set; }
}

/// <summary>This device's durable clock and sequence. Never synchronized.</summary>
public class LeewayDeviceRow
{
    /// <summary>The fixed ID of the single row.</summary>
    public const string SingleId = "device";

    /// <summary>The row ID.</summary>
    public string Id { get; set; } = SingleId;

    /// <summary>The last sequence number issued.</summary>
    public long Sequence { get; set; }

    /// <summary>The clock's physical time.</summary>
    public long ClockPhysical { get; set; }

    /// <summary>The clock's logical time.</summary>
    public int ClockLogical { get; set; }

    /// <summary>The physical part of the highest server stamp seen.</summary>
    public long ObservedPhysical { get; set; }

    /// <summary>The logical part of the highest server stamp seen.</summary>
    public int ObservedLogical { get; set; }

    /// <summary>The replica part of the highest server stamp seen.</summary>
    public string ObservedReplica { get; set; } = string.Empty;
}

/// <summary>Leeway's client settings.</summary>
public sealed class LeewayClientOptions
{
    /// <summary>A stable, unique ID for this device. There is no authentication, so the application must secure it.</summary>
    public required string DeviceId { get; init; }

    /// <summary>The HTTP client, with its base address set. Its timeout is what decides "no signal" on a poor connection.</summary>
    public required HttpClient HttpClient { get; init; }

    /// <summary>The time source for the hybrid logical clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The operations table's address on the server.</summary>
    public Uri OperationsEndpoint { get; init; } = new("/tables/leewayoperations", UriKind.Relative);

    /// <summary>The leases table's address on the server.</summary>
    public Uri LeasesEndpoint { get; init; } = new("/tables/leewayleases", UriKind.Relative);
}

/// <summary>The base class for an application's offline store. Add a DbSet for each <see cref="OfflineEntity"/> type.</summary>
public abstract class LeewayDbContext : OfflineDbContext
{
    private readonly LeewayClientOptions leeway;

    /// <summary>Creates the context.</summary>
    /// <param name="options">The EF Core options.</param>
    /// <param name="leeway">Leeway's settings.</param>
    protected LeewayDbContext(DbContextOptions options, LeewayClientOptions leeway) : base(options) => this.leeway = leeway;

    /// <summary>Operations being sent.</summary>
    public DbSet<LeewayOperationRow> LeewayOperations => Set<LeewayOperationRow>();

    /// <summary>This device's leases.</summary>
    public DbSet<LeewayLeaseRow> LeewayLeases => Set<LeewayLeaseRow>();

    /// <summary>This device's record of its operations.</summary>
    [DoNotSynchronize]
    public DbSet<LeewayOutboxRow> LeewayOutbox => Set<LeewayOutboxRow>();

    /// <summary>What this device has done with each resource.</summary>
    [DoNotSynchronize]
    public DbSet<LeewayWalletRow> LeewayWallets => Set<LeewayWalletRow>();

    /// <summary>This device's clock and sequence.</summary>
    [DoNotSynchronize]
    public DbSet<LeewayDeviceRow> LeewayDevice => Set<LeewayDeviceRow>();

    /// <summary>The entity types with merge semantics in this store.</summary>
    internal IEnumerable<Type> OfflineEntityTypes
        => Model.GetEntityTypes().Select(t => t.ClrType).Where(t => typeof(OfflineEntity).IsAssignableFrom(t));

    /// <inheritdoc />
    protected sealed override void OnDatasyncInitialization(DatasyncOfflineOptionsBuilder optionsBuilder)
    {
        string device = this.leeway.DeviceId;
        optionsBuilder.UseHttpClient(this.leeway.HttpClient);
        optionsBuilder.Entity<LeewayOperationRow>(cfg =>
        {
            cfg.Endpoint = this.leeway.OperationsEndpoint;

            // A 409 means this operation was already applied, and carries its stored outcome (probe C14).
            cfg.ConflictResolver = new ServerWinsConflictResolver();
        });
        optionsBuilder.Entity<LeewayLeaseRow>(cfg =>
        {
            cfg.Endpoint = this.leeway.LeasesEndpoint;
            cfg.Query = cfg.Query.Where(x => x.Device == device);
        });
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseDirectEdits();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseDirectEdits();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>A direct edit would queue a whole-row write that the server refuses, which would then block pulls (probe C06).</summary>
    private void RefuseDirectEdits()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<OfflineEntity>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Merged entities cannot be saved directly. Use LeewayClient.EditAsync or TrySpendAsync.");
        }
    }
}
