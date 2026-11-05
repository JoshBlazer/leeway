using System.Linq.Expressions;
using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Leeway.Datasync.Server;

/// <summary>Authenticated clients may read but never write. Projected rows change only through operations.</summary>
/// <typeparam name="T">The row type.</typeparam>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
public sealed class ReadOnlyAccess<T, TContext>(LeewayAuthority<TContext> authority) : AccessControlProvider<T>
    where T : ITableData
    where TContext : DbContext
{
    /// <inheritdoc />
    public override ValueTask<bool> IsAuthorizedAsync(TableOperation operation, T? entity, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(operation is TableOperation.Query or TableOperation.Read && (authority.IsOpen || authority.CurrentDevice is not null));
}

/// <summary>Leases are read-only, and a device sees only its own.</summary>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
public sealed class LeaseAccess<TContext>(LeewayAuthority<TContext> authority) : AccessControlProvider<LeewayLeaseRow> where TContext : DbContext
{
    /// <inheritdoc />
    public override ValueTask<bool> IsAuthorizedAsync(TableOperation operation, LeewayLeaseRow? entity, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(operation is TableOperation.Query or TableOperation.Read && (authority.IsOpen || authority.CurrentDevice is not null));

    /// <inheritdoc />
    public override Expression<Func<LeewayLeaseRow, bool>>? GetDataView()
    {
        if (authority.IsOpen)
        {
            return null;
        }

        string? device = authority.CurrentDevice;
        return x => x.Device == device;
    }
}

/// <summary>Clients may only append, and only as themselves. A recorded operation cannot be rewritten or deleted.</summary>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
public sealed class AppendOnlyAccess<TContext>(LeewayAuthority<TContext> authority) : AccessControlProvider<LeewayOperationRow> where TContext : DbContext
{
    /// <inheritdoc />
    public override ValueTask<bool> IsAuthorizedAsync(TableOperation operation, LeewayOperationRow? entity, CancellationToken cancellationToken = default)
    {
        if (operation != TableOperation.Create)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(authority.IsOpen || (authority.CurrentDevice is { } device && device == entity?.Device));
    }

    /// <summary>A replay of another device's operation ID must not reveal the stored row.</summary>
    /// <returns>The rows this caller may see.</returns>
    public override Expression<Func<LeewayOperationRow, bool>>? GetDataView()
    {
        if (authority.IsOpen)
        {
            return null;
        }

        string? device = authority.CurrentDevice;
        return x => x.Device == device;
    }
}

/// <summary>Stores an operation by applying it, in one transaction.</summary>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
public sealed class LeewayOperationRepository<TContext>(TContext context, LeewayAuthority<TContext> authority)
    : EntityTableRepository<LeewayOperationRow>(context) where TContext : DbContext
{
    /// <inheritdoc />
    public override async ValueTask CreateAsync(LeewayOperationRow entity, CancellationToken cancellationToken = default)
        => await authority.ApplyAsync(entity, cancellationToken);
}

/// <summary>Receives operations. Derive a class with a [Route] matching the client's OperationsEndpoint.</summary>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
/// <param name="context">The DbContext.</param>
/// <param name="authority">The authority.</param>
public abstract class LeewayOperationsController<TContext>(TContext context, LeewayAuthority<TContext> authority)
    : TableController<LeewayOperationRow>(new LeewayOperationRepository<TContext>(context, authority), new AppendOnlyAccess<TContext>(authority))
    where TContext : DbContext;

/// <summary>Serves leases. Derive a class with a [Route] matching the client's LeasesEndpoint.</summary>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
/// <param name="context">The DbContext.</param>
/// <param name="authority">The authority.</param>
public abstract class LeewayLeasesController<TContext>(TContext context, LeewayAuthority<TContext> authority)
    : TableController<LeewayLeaseRow>(new EntityTableRepository<LeewayLeaseRow>(context), new LeaseAccess<TContext>(authority))
    where TContext : DbContext;

/// <summary>Serves an entity's projected rows. Derive a class with a [Route] for the entity.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
/// <param name="context">The DbContext.</param>
/// <param name="authority">The authority.</param>
public abstract class LeewayEntityController<TEntity, TContext>(TContext context, LeewayAuthority<TContext> authority)
    : TableController<TEntity>(new EntityTableRepository<TEntity>(context), new ReadOnlyAccess<TEntity, TContext>(authority), new TableControllerOptions { EnableSoftDelete = true })
    where TEntity : LeewayEntity
    where TContext : DbContext;
