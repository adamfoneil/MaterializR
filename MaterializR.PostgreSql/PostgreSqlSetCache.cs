using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace MaterializR.PostgreSql;

/// <summary>
/// PostgreSQL implementation of stale-row upserts for a set cache.
/// The mapped tenant and non-nullable dimension columns must have a matching unique key or index.
/// </summary>
public abstract class PostgreSqlSetCache<TDbContext, TCacheEntity, TCacheDimensions, TTenantKey>(
    ILogger<PostgreSqlSetCache<TDbContext, TCacheEntity, TCacheDimensions, TTenantKey>> logger,
    IDbContextFactory<TDbContext> dbFactory)
    : SetCache<TDbContext, TCacheEntity, TCacheDimensions, TTenantKey>(logger, dbFactory)
    where TDbContext : DbContext
    where TCacheEntity : class, ITenantCacheEntity<TTenantKey>
    where TCacheDimensions : notnull
    where TTenantKey : notnull
{
    /// <summary>
    /// Selects the mapped properties that identify a dimension row and participate in its unique tenant key.
    /// </summary>
    protected abstract Expression<Func<TCacheEntity, object?>> DimensionsKeySelector { get; }

    protected sealed override Task UpsertStaleAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] rows, CancellationToken cancellationToken) =>
        PostgreSqlStaleUpsert.ExecuteAsync(dbContext, tenantId, rows, DimensionsKeySelector, cancellationToken);
}
