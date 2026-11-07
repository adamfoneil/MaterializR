using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace MaterializR.Core;

/// <summary>
/// caches calculations derived from a single row, isolated per tenant
/// </summary>
public abstract class RowCache<TDbContext, TCacheEntity, TKey, TTenantKey>(
    ILogger<RowCache<TDbContext, TCacheEntity, TKey, TTenantKey>> logger,
    IDbContextFactory<TDbContext> dbFactory)
    where TDbContext : DbContext
    where TCacheEntity : class, ITenantCacheEntity<TTenantKey>, new()
    where TKey : notnull
    where TTenantKey : notnull
{
    protected readonly ILogger<RowCache<TDbContext, TCacheEntity, TKey, TTenantKey>> Logger = logger;

    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;

    protected abstract DbSet<TCacheEntity> CacheDbSet(TDbContext db);

    /// <summary>
    /// query that returns the live data for a set of keys. Source queries must be scoped to the tenant
    /// </summary>
    protected abstract Task<TCacheEntity[]> QueryLiveAsync(TDbContext dbContext, TTenantKey tenantId, TKey[] keys);

    /// <summary>
    /// create a new cache row for a key. <see cref="ITenantCacheEntity{TTenantKey}.TenantId"/> is set for you
    /// </summary>
    protected abstract TCacheEntity FromKey(TKey key);

    protected abstract Expression<Func<TCacheEntity, TKey>> KeySelector { get; }

    protected abstract void UpdateFacts(TCacheEntity existingRow, TCacheEntity incomingRow);

    protected virtual async Task MergeAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] liveData)
    {
        var keySelector = KeySelector.Compile();
        var incomingByKey = liveData
            .GroupBy(keySelector)
            .ToDictionary(g => g.Key, g => g.Last());

        var staleRows = await CacheDbSet(dbContext)
            .ForTenant(tenantId)
            .Where(row => row.IsStale)
            .ToArrayAsync();

        foreach (var staleRow in staleRows)
        {
            var key = keySelector(staleRow);

            if (incomingByKey.TryGetValue(key, out var incomingRow))
            {
                UpdateFacts(staleRow, incomingRow);
                staleRow.IsStale = true;
                incomingByKey.Remove(key);
            }
            else
            {
                CacheDbSet(dbContext).Remove(staleRow);
            }
        }

        if (incomingByKey.Count > 0)
        {
            foreach (var row in incomingByKey.Values) row.TenantId = tenantId;
            CacheDbSet(dbContext).AddRange(incomingByKey.Values);
        }

        await dbContext.SaveChangesAsync();
    }

    public async Task<int> MarkStaleKeysAsync(TDbContext dbContext, TTenantKey tenantId, (bool Inserted, TKey Key)[] keys)
    {
        if (!keys.Any()) return 0;

        return await Util.TrackDuration(Logger, async () =>
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Marking stale keys: {keys}", string.Join(", ", keys.Select(k => k.Key)));
            }            

            var insertedKeys = keys
                .Where(k => k.Inserted)
                .Select(k => k.Key)
                .Distinct()
                .ToArray();

            if (insertedKeys.Length > 0)
            {
                CacheDbSet(dbContext).AddRange(insertedKeys.Select(key =>
                {
                    var row = FromKey(key);
                    row.TenantId = tenantId;
                    row.IsStale = true;
                    return row;
                }));
            }

            int inserted = await dbContext.SaveChangesAsync();

            var updatedKeys = keys
                .Where(k => !k.Inserted)
                .Select(k => k.Key)
                .Distinct()
                .ToArray();

            int updated = 0;
            if (updatedKeys.Length > 0)
            {
                var updatedSet = updatedKeys.ToHashSet();
                var keySelector = KeySelector.Compile();

                var rows = await CacheDbSet(dbContext).ForTenant(tenantId).ToArrayAsync();
                foreach (var row in rows)
                {
                    if (updatedSet.Contains(keySelector(row)))
                    {
                        row.IsStale = true;
                        dbContext.Entry(row).Property(nameof(ICacheEntity.IsStale)).IsModified = true;
                    }
                }

                updated = await dbContext.SaveChangesAsync();
            }

            return inserted + updated;
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
                var keys = await CacheDbSet(db)
                    .AsNoTracking()
                    .ForTenant(tenantId)
                    .Where(e => e.IsStale)
                    .Select(KeySelector)
                    .ToArrayAsync();

                var liveData = await QueryLiveAsync(db, tenantId, keys);
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
