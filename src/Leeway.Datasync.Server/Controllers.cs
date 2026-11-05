using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Leeway.Datasync.Server;

/// <summary>Clients may read but never write. Projected rows change only through operations.</summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed class ReadOnlyAccess<T> : AccessControlProvider<T> where T : ITableData
{
    /// <inheritdoc />
    public override ValueTask<bool> IsAuthorizedAsync(TableOperation operation, T? entity, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(operation is TableOperation.Query or TableOperation.Read);
}

/// <summary>Clients may only append. A recorded operation cannot be rewritten or deleted.</summary>
public sealed class AppendOnlyAccess : AccessControlProvider<LeewayOperationRow>
{
    /// <inheritdoc />
    public override ValueTask<bool> IsAuthorizedAsync(TableOperation operation, LeewayOperationRow? entity, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(operation == TableOperation.Create);
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
    : TableController<LeewayOperationRow>(new LeewayOperationRepository<TContext>(context, authority), new AppendOnlyAccess())
    where TContext : DbContext;

/// <summary>Serves leases. Derive a class with a [Route] matching the client's LeasesEndpoint.</summary>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
/// <param name="context">The DbContext.</param>
public abstract class LeewayLeasesController<TContext>(TContext context)
    : TableController<LeewayLeaseRow>(new EntityTableRepository<LeewayLeaseRow>(context), new ReadOnlyAccess<LeewayLeaseRow>())
    where TContext : DbContext;

/// <summary>Serves an entity's projected rows. Derive a class with a [Route] for the entity.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
/// <param name="context">The DbContext.</param>
public abstract class LeewayEntityController<TEntity, TContext>(TContext context)
    : TableController<TEntity>(new EntityTableRepository<TEntity>(context), new ReadOnlyAccess<TEntity>())
    where TEntity : LeewayEntity
    where TContext : DbContext;
