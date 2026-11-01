using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace MaterializR.Core;

public abstract class RolldownConfiguration<TDbContext, TCacheEntity, TKey>(
    ILogger<RolldownConfiguration<TDbContext, TCacheEntity, TKey>> logger,
    IDbContextFactory<TDbContext> dbFactory)
    where TDbContext : DbContext
    where TCacheEntity : class, ICacheEntity, new()
{
    protected readonly ILogger<RolldownConfiguration<TDbContext, TCacheEntity, TKey>> Logger = logger;

    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;

    protected abstract DbSet<TCacheEntity> RolldownDbSet(TDbContext db);

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

        var staleRows = await RolldownDbSet(dbContext)
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
                RolldownDbSet(dbContext).Remove(staleRow);
            }
        }

        if (incomingByKey.Count > 0)
        {
            RolldownDbSet(dbContext).AddRange(incomingByKey.Values);
        }

        await dbContext.SaveChangesAsync();
    }

    public (bool Inserted, TKey Key)[] GetAffectedKeys(ChangeTracker changeTracker, Func<object, TKey> selector) =>
        [.. changeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => (e.State == EntityState.Added, selector(e.Entity)))
            .Distinct()
            .Where(val => !EqualityComparer<TKey>.Default.Equals(val.Item2, default))];

    public async Task<int> MarkStaleKeysAsync(TDbContext dbContext, (bool Inserted, TKey Key)[] keys)
    {
        if (!keys.Any()) return 0;

        return await Util.TrackDuration(Logger, async () =>
        {
            RolldownDbSet(dbContext).AddRange(keys.Where(k => k.Inserted).Select(k => FromKey(k.Key)));
            int inserted = await dbContext.SaveChangesAsync();

            int updated = await RolldownDbSet(dbContext)
                .Where(row => keys.Any(k => !k.Inserted))
                .ExecuteUpdateAsync(p => p.SetProperty(row => row.IsStale, true));

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
                var keys = await RolldownDbSet(db)
                    .AsNoTracking()
                    .Where(e => e.IsStale)
                    .Select(KeySelector)
                    .ToArrayAsync();

                var liveData = await QueryLiveAsync(db, keys);
                await MergeAsync(db, liveData);

                int result = await RolldownDbSet(db).Where(row => row.IsStale).ExecuteUpdateAsync(p => p.SetProperty(row => row.IsStale, false));
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
