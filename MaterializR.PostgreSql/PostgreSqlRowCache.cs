using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MaterializR.PostgreSql;

/// <summary>
/// PostgreSQL implementation of stale-row upserts for a row cache.
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
    protected sealed override Task UpsertStaleAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] rows, CancellationToken cancellationToken) =>
        PostgreSqlStaleUpsert.ExecuteAsync(dbContext, tenantId, rows, KeySelector, cancellationToken);
}
