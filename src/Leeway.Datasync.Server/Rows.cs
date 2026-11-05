using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Leeway.Datasync.Server;

/// <summary>The base class for a server entity with merge semantics. Clients read these rows and never write them.</summary>
public abstract class LeewayEntity : BaseEntityTableData
{
    /// <summary>The entity's merge state, as JSON. The other columns are projected from it.</summary>
    public string? LeewayState { get; set; }
}

/// <summary>One operation in the append-only table. Its ID is the operation's UUID, which makes a replay harmless.</summary>
public class LeewayOperationRow : BaseEntityTableData
{
    /// <summary>The device that made the operation.</summary>
    [Required]
    public string Device { get; set; } = string.Empty;

    /// <summary>The operation, as JSON.</summary>
    [Required]
    public string Payload { get; set; } = string.Empty;

    /// <summary>The device's sequence number, for retention. Set by the server.</summary>
    public long Sequence { get; set; }

    /// <summary>What the server did. A replay returns this stored outcome.</summary>
    public string? OutcomeStatus { get; set; }

    /// <summary>The amount involved in the outcome.</summary>
    public long OutcomeAmount { get; set; }

    /// <summary>The stamp of the entity row that includes this operation, or zero if the row did not change.</summary>
    public long EntityStamp { get; set; }
}

/// <summary>A device's cumulative grant on a resource. A device pulls only its own.</summary>
public class LeewayLeaseRow : BaseEntityTableData
{
    /// <summary>The resource.</summary>
    [Required]
    public string Resource { get; set; } = string.Empty;

    /// <summary>The device.</summary>
    [Required]
    public string Device { get; set; } = string.Empty;

    /// <summary>The total granted.</summary>
    public long Granted { get; set; }
}

/// <summary>A resource's ledger. Private to the server.</summary>
public class LeewayEscrowRow
{
    /// <summary>The resource name.</summary>
    [Key]
    public string Resource { get; set; } = string.Empty;

    /// <summary>The ledger, as JSON.</summary>
    [Required]
    public string Ledger { get; set; } = string.Empty;

    /// <summary>The entity type name.</summary>
    [Required]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The entity ID.</summary>
    [Required]
    public string EntityId { get; set; } = string.Empty;

    /// <summary>The field name.</summary>
    [Required]
    public string Field { get; set; } = string.Empty;
}

/// <summary>How far each device says it has been acknowledged, which is what makes pruning safe. Private to the server.</summary>
public class LeewayDeviceProgressRow
{
    /// <summary>The device.</summary>
    [Key]
    public string Device { get; set; } = string.Empty;

    /// <summary>The device has its answer for every sequence below this, so it will not send those again.</summary>
    public long SettledBelow { get; set; }
}

/// <summary>Server-wide state: the last row stamp and stamp admission. Private to the server.</summary>
public class LeewayAuthorityRow
{
    /// <summary>The fixed ID of the single row.</summary>
    public const string SingleId = "authority";

    /// <summary>The row ID.</summary>
    [Key]
    public string Id { get; set; } = SingleId;

    /// <summary>The last row stamp issued. Stamps strictly increase (ADR-0013).</summary>
    public long LastStampMs { get; set; }

    /// <summary>The stamp authority, as JSON.</summary>
    public string? Stamps { get; set; }
}

/// <summary>Model configuration.</summary>
public static class LeewayModelBuilderExtensions
{
    private static readonly ValueConverter<DateTimeOffset, long> UnixMilliseconds = new(
        v => v.ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    /// <summary>Adds Leeway's tables. Call this from OnModelCreating.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <returns>The model builder.</returns>
    public static ModelBuilder ApplyLeewayServer(this ModelBuilder modelBuilder)
    {
        Configure(modelBuilder.Entity<LeewayOperationRow>());
        Configure(modelBuilder.Entity<LeewayLeaseRow>());
        modelBuilder.Entity<LeewayEscrowRow>();
        modelBuilder.Entity<LeewayAuthorityRow>();
        modelBuilder.Entity<LeewayDeviceProgressRow>();
        return modelBuilder;
    }

    /// <summary>Configures an entity with merge semantics. Call this from OnModelCreating.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="modelBuilder">The model builder.</param>
    /// <returns>The model builder.</returns>
    public static ModelBuilder LeewayEntity<T>(this ModelBuilder modelBuilder) where T : LeewayEntity
    {
        Configure(modelBuilder.Entity<T>());
        return modelBuilder;
    }

    private static void Configure<T>(EntityTypeBuilder<T> entity) where T : BaseEntityTableData
    {
        // SQLite cannot compare or order DateTimeOffset, and pull does both on UpdatedAt.
        entity.Property(x => x.UpdatedAt).HasConversion(UnixMilliseconds);
        entity.Property(x => x.Version).IsConcurrencyToken();
    }
}
