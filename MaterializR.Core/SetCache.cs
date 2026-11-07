using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MaterializR.Core;

public record DimensionChangesResult(int Inserted, int Updated);

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
    protected abstract Task<TCacheEntity[]> QueryLiveAsync(TDbContext dbContext, TTenantKey tenantId, TCacheDimensions[] dimensions);

    /// <summary>
    /// upsert queried live data into the cache/view table
    /// </summary>
    protected virtual async Task MergeAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        var incomingByDimensions = data
            .GroupBy(row => ToDimensions(row))
            .ToDictionary(g => g.Key, g => g.Last());

        var dimensions = incomingByDimensions.Keys.ToArray();
        var existingRows = await ExistingRowsAsync(dbContext, tenantId, dimensions);
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

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// fetch existing rollup rows of a tenant for a dimension batch so they can be marked modified.
    /// Overrides must restrict results to the tenant, for example with <c>.Where(row => row.TenantId.Equals(tenantId))</c>
    /// </summary>
    protected virtual Task<TCacheEntity[]> ExistingRowsAsync(TDbContext dbContext, TTenantKey tenantId, TCacheDimensions[] dimensions) =>
        CacheDbSet(dbContext).ForTenant(tenantId).ToArrayAsync();

    /// <summary>
    /// flag dimensions as modified, inserting where they don't exist
    /// </summary>
    public async Task<DimensionChangesResult> MarkStaleDimensionsAsync(TDbContext dbContext, TTenantKey tenantId, (bool Inserted, TCacheDimensions Dimensions)[] changes)
    {
        if (!changes.Any()) return new(0, 0);

        return await Util.TrackDuration(Logger, async () =>
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                var logDetail = string.Join(", ", changes.GroupBy(c => c.Inserted).Select(g => $"{(g.Key ? "Inserted" : "Updated")}: {string.Join(", ", g.Select(c => c.Dimensions))}"));
                Logger.LogInformation("Marking stale dimensions for tenant {tenantId}: {dimensions}", tenantId, logDetail);
            }

            foreach (var (_, dim) in changes.Where(d => d.Inserted).Distinct())
            {
                var row = FromDimensions(dim);
                row.TenantId = tenantId;
                CacheDbSet(dbContext).Add(row);
            }

            var modifiedDimensions = changes
                .Where(d => !d.Inserted)
                .Select(d => d.Dimensions)
                .Distinct()
                .ToArray();

            if (modifiedDimensions.Length > 0)
            {
                var modifiedLookup = modifiedDimensions.ToHashSet();
                var existingRows = await ExistingRowsAsync(dbContext, tenantId, modifiedDimensions);

                foreach (var entity in existingRows)
                {
                    if (modifiedLookup.Contains(ToDimensions(entity)))
                    {
                        entity.IsStale = true;
                        dbContext.Entry(entity).Property(nameof(ICacheEntity.IsStale)).IsModified = true;
                    }
                }
            }

            await dbContext.SaveChangesAsync();

            return new DimensionChangesResult(
                Inserted: changes.Count(d => d.Inserted),
                Updated: changes.Count(d => !d.Inserted));
        });
    }

    /// <summary>
    /// refreshes the stale rows of a single tenant
    /// </summary>
    public async Task<int> UpdateAsync(TTenantKey tenantId)
    {
        using var db = DbFactory.CreateDbContext();
        using var txn = db.Database.BeginTransaction();

        return await Util.TrackDuration(Logger, async () =>
        {
            try
            {
                var dimensions = await CacheDbSet(db).AsNoTracking().ForTenant(tenantId).Where(row => row.IsStale).ToArrayAsync();
                if (!dimensions.Any()) return 0;
                var dimensionKeys = dimensions.Select(row => ToDimensions(row)).ToArray();
                var liveData = await QueryLiveAsync(db, tenantId, dimensionKeys);
                await MergeAsync(db, tenantId, liveData);
                int result = await CacheDbSet(db).ForTenant(tenantId).Where(row => row.IsStale).ExecuteUpdateAsync(p => p.SetProperty(row => row.IsStale, false));
                await txn.CommitAsync();

                return result;
            }
            catch (Exception exc)
            {
                await txn.RollbackAsync();
                Logger.LogError(exc, "Error in UpdateAsync");                
                throw;
            }
        });        
    }

}
