using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace MaterializR.Core;

public abstract class RowCache<TDbContext, TCacheEntity, TKey>(
    ILogger<RowCache<TDbContext, TCacheEntity, TKey>> logger,
    IDbContextFactory<TDbContext> dbFactory)
    where TDbContext : DbContext
    where TCacheEntity : class, ICacheEntity, new()
    where TKey : notnull
{
    protected readonly ILogger<RowCache<TDbContext, TCacheEntity, TKey>> Logger = logger;

    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;

    protected abstract DbSet<TCacheEntity> CacheDbSet(TDbContext db);

    protected abstract Task<TCacheEntity[]> QueryLiveAsync(TDbContext dbContext, TKey[] keys);

    protected abstract TCacheEntity FromKey(TKey key);

    protected abstract Expression<Func<TCacheEntity, TKey>> KeySelector { get; }

    protected abstract void UpdateFacts(TCacheEntity existingRow, TCacheEntity incomingRow);

    protected virtual async Task MergeAsync(TDbContext dbContext, TCacheEntity[] liveData)
    {
        var keySelector = KeySelector.Compile();
        var incomingByKey = liveData
            .GroupBy(keySelector)
            .ToDictionary(g => g.Key, g => g.Last());

        var staleRows = await CacheDbSet(dbContext)
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
            CacheDbSet(dbContext).AddRange(incomingByKey.Values);
        }

        await dbContext.SaveChangesAsync();
    }

    public async Task<int> MarkStaleKeysAsync(TDbContext dbContext, (bool Inserted, TKey Key)[] keys)
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

                var rows = await CacheDbSet(dbContext).ToArrayAsync();
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

    public async Task<int> UpdateAsync()
    {
        using var db = DbFactory.CreateDbContext();
        using var txn = db.Database.BeginTransaction();

        return await Util.TrackDuration(Logger, async () =>
        {
            try
            {
                var keys = await CacheDbSet(db)
                    .AsNoTracking()
                    .Where(e => e.IsStale)
                    .Select(KeySelector)
                    .ToArrayAsync();

                var liveData = await QueryLiveAsync(db, keys);
                await MergeAsync(db, liveData);

                int result = await CacheDbSet(db).Where(row => row.IsStale).ExecuteUpdateAsync(p => p.SetProperty(row => row.IsStale, false));
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
