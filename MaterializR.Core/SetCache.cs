using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MaterializR.Core;

/// <summary>
/// caches rollups over sets of rows along dimensions, isolated per tenant
/// </summary>
public abstract class SetCache<TDbContext, TCacheEntity, TCacheDimensions, TTenantKey>(
    ILogger<SetCache<TDbContext, TCacheEntity, TCacheDimensions, TTenantKey>> logger,
    IDbContextFactory<TDbContext> dbFactory)
    where TDbContext : DbContext
    where TCacheEntity : class, ITenantCacheEntity<TTenantKey>
    where TCacheDimensions : notnull
    where TTenantKey : notnull
{
    protected readonly ILogger<SetCache<TDbContext, TCacheEntity, TCacheDimensions, TTenantKey>> Logger = logger;

    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;

    protected abstract DbSet<TCacheEntity> CacheDbSet(TDbContext db);

    /// <summary>Atomically inserts stale rows or marks existing rows stale without changing their facts.</summary>
    /// <remarks>Scope to the supplied tenant and transaction, accept empty input, and do not use EF tracking.</remarks>
    protected abstract Task UpsertStaleAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] rows, CancellationToken cancellationToken);

    /// <summary>Atomically upserts live facts and clears the stale flag for the supplied tenant.</summary>
    /// <remarks>Scope to the supplied tenant and transaction, accept empty input, and do not use EF tracking.</remarks>
    protected abstract Task UpsertFactsAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] rows, CancellationToken cancellationToken);

    /// <summary>Selects stale rows inside the refresh transaction. Providers can override this to lock them.</summary>
    protected virtual Task<TCacheEntity[]> LockStaleRowsAsync(TDbContext dbContext, TTenantKey tenantId, CancellationToken cancellationToken) =>
        CacheDbSet(dbContext).AsNoTracking().ForTenant(tenantId).Where(row => row.IsStale).ToArrayAsync(cancellationToken);

    /// <summary>
    /// create a new cache row for a set of dimensions. <see cref="ITenantCacheEntity{TTenantKey}.TenantId"/> is set for you
    /// </summary>
    protected abstract TCacheEntity FromDimensions(TCacheDimensions dimensions);

    protected abstract TCacheDimensions ToDimensions(TCacheEntity entity);

    /// <summary>
    /// query that returns the live view data for a set of dimensions. Source queries must be scoped to the tenant
    /// </summary>
    protected abstract Task<TCacheEntity[]> QueryLiveAsync(TDbContext dbContext, TTenantKey tenantId, TCacheDimensions[] dimensions, CancellationToken cancellationToken);

    /// <summary>
    /// flag dimensions as modified, inserting where they don't exist
    /// </summary>
    public async Task MarkStaleDimensionsAsync(TDbContext dbContext, TTenantKey tenantId, TCacheDimensions[] dimensions, CancellationToken cancellationToken = default)
    {
        if (!dimensions.Any()) return;

        await Util.LogDuration(Logger, async () =>
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Marking stale dimensions for tenant {tenantId}: {dimensions}", tenantId, string.Join(", ", dimensions));
            }

            var rows = dimensions.Distinct().Select(dim =>
            {
                var row = FromDimensions(dim);
                row.TenantId = tenantId;
                row.IsStale = true;
                return row;
            }).ToArray();

            await UpsertStaleAsync(dbContext, tenantId, rows, cancellationToken);
        });
    }

    /// <summary>
    /// refreshes the stale rows of a single tenant
    /// </summary>
    public async Task<int> UpdateAsync(TTenantKey tenantId, CancellationToken cancellationToken = default)
    {
        using var db = DbFactory.CreateDbContext();
        await using var txn = await db.Database.BeginTransactionAsync(cancellationToken);

        return await Util.LogDuration(Logger, async () =>
        {
            try
            {
                var staleRows = await LockStaleRowsAsync(db, tenantId, cancellationToken);
                var dimensions = staleRows.Select(ToDimensions).ToHashSet();
                if (dimensions.Count == 0)
                {
                    await txn.CommitAsync(cancellationToken);
                    return 0;
                }
                var liveData = (await QueryLiveAsync(db, tenantId, [.. dimensions], cancellationToken))
                    .Where(row => dimensions.Contains(ToDimensions(row)))
                    .GroupBy(ToDimensions).Select(group => group.Last()).ToArray();
                await UpsertFactsAsync(db, tenantId, liveData, cancellationToken);

                var refreshed = liveData.Select(ToDimensions).ToHashSet();
                var orphans = staleRows.Where(row => !refreshed.Contains(ToDimensions(row))).ToArray();
                var deleted = await Util.DeleteStaleRowsAsync(db, CacheDbSet(db), tenantId, orphans, cancellationToken);
                await txn.CommitAsync(cancellationToken);

                return liveData.Length + deleted;
            }
            catch (Exception exc)
            {
                await txn.RollbackAsync(CancellationToken.None);
                Logger.LogError(exc, "Error in UpdateAsync");
                throw;
            }
        });
    }

}
