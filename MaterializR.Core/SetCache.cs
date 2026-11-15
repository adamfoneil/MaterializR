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

    protected abstract Task UpsertStaleAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] rows, CancellationToken cancellationToken);

    /// <summary>
    /// create a new cache row for a set of dimensions. <see cref="ITenantCacheEntity{TTenantKey}.TenantId"/> is set for you
    /// </summary>
    protected abstract TCacheEntity FromDimensions(TCacheDimensions dimensions);

    protected abstract TCacheDimensions ToDimensions(TCacheEntity entity);

    /// <summary>
    /// apply incoming values to an existing row, excluding key properties
    /// </summary>
    protected abstract void UpdateFacts(TCacheEntity existingRow, TCacheEntity incomingRow);

    /// <summary>
    /// query that returns the live view data for a set of dimensions. Source queries must be scoped to the tenant
    /// </summary>
    protected abstract Task<TCacheEntity[]> QueryLiveAsync(TDbContext dbContext, TTenantKey tenantId, TCacheDimensions[] dimensions, CancellationToken cancellationToken);

    /// <summary>
    /// upsert queried live data into the cache/view table
    /// </summary>
    protected virtual async Task MergeAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] data, CancellationToken cancellationToken = default)
    {
        if (data.Length == 0)
        {
            return;
        }

        var incomingByDimensions = data
            .GroupBy(row => ToDimensions(row))
            .ToDictionary(g => g.Key, g => g.Last());

        var dimensions = incomingByDimensions.Keys.ToArray();
        var existingRows = await ExistingRowsAsync(dbContext, tenantId, dimensions, cancellationToken);
        var existingByDimensions = existingRows.ToDictionary(row => ToDimensions(row));
        var dbSet = CacheDbSet(dbContext);

        foreach (var (dimension, incomingRow) in incomingByDimensions)
        {
            if (existingByDimensions.TryGetValue(dimension, out var existingRow))
            {
                UpdateFacts(existingRow, incomingRow);
            }
            else
            {
                incomingRow.TenantId = tenantId;
                dbSet.Add(incomingRow);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// fetch existing rollup rows of a tenant for a dimension batch so they can be marked modified.
    /// Overrides must restrict results to the tenant, for example with <c>.Where(row => row.TenantId.Equals(tenantId))</c>
    /// </summary>
    protected virtual Task<TCacheEntity[]> ExistingRowsAsync(TDbContext dbContext, TTenantKey tenantId, TCacheDimensions[] dimensions, CancellationToken cancellationToken) =>
        CacheDbSet(dbContext).ForTenant(tenantId).ToArrayAsync(cancellationToken);

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
                var dimensions = await CacheDbSet(db).AsNoTracking().ForTenant(tenantId).Where(row => row.IsStale).ToArrayAsync(cancellationToken);
                if (!dimensions.Any()) return 0;
                var dimensionKeys = dimensions.Select(row => ToDimensions(row)).ToArray();
                var liveData = await QueryLiveAsync(db, tenantId, dimensionKeys, cancellationToken);

                await MergeAsync(db, tenantId, liveData, cancellationToken);
                int result = await CacheDbSet(db).ForTenant(tenantId).Where(row => row.IsStale).ExecuteUpdateAsync(p => p.SetProperty(row => row.IsStale, false), cancellationToken);
                await txn.CommitAsync(cancellationToken);

                return result;
            }
            catch (Exception exc)
            {
                await txn.RollbackAsync(cancellationToken);
                Logger.LogError(exc, "Error in UpdateAsync");
                throw;
            }
        });
    }

}
