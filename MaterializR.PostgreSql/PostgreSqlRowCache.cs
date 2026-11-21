using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace MaterializR.PostgreSql;

/// <summary>
/// PostgreSQL implementation of stale-row upserts for a row cache.
/// Upserts bypass EF tracking; tracked entities can remain stale and should be reloaded as needed.
/// Inserted shadow properties require a configured default, except for the discriminator.
/// The mapped tenant and non-nullable key columns must have a matching unique key or index.
/// </summary>
public abstract class PostgreSqlRowCache<TDbContext, TCacheEntity, TKey, TTenantKey>(
    ILogger<PostgreSqlRowCache<TDbContext, TCacheEntity, TKey, TTenantKey>> logger,
    IDbContextFactory<TDbContext> dbFactory)
    : RowCache<TDbContext, TCacheEntity, TKey, TTenantKey>(logger, dbFactory)
    where TDbContext : DbContext
    where TCacheEntity : class, ITenantCacheEntity<TTenantKey>, new()
    where TKey : notnull
    where TTenantKey : notnull
{
    /// <summary>Selects the mapped fact properties written by refresh upserts.</summary>
    protected abstract Expression<Func<TCacheEntity, object?>> FactProperties { get; }

    protected sealed override Task UpsertStaleAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] rows, CancellationToken cancellationToken) =>
        PostgreSqlStaleUpsert.ExecuteAsync(dbContext, tenantId, rows, KeySelector, FactProperties, refresh: false, cancellationToken);

    protected sealed override Task UpsertFactsAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] rows, CancellationToken cancellationToken) =>
        PostgreSqlStaleUpsert.ExecuteAsync(dbContext, tenantId, rows, KeySelector, FactProperties, refresh: true, cancellationToken);

    protected sealed override Task<TCacheEntity[]> LockStaleRowsAsync(TDbContext dbContext, TTenantKey tenantId, CancellationToken cancellationToken) =>
        PostgreSqlStaleUpsert.LockStaleRowsAsync<TDbContext, TCacheEntity, TTenantKey>(dbContext, tenantId, KeySelector, cancellationToken);
}
