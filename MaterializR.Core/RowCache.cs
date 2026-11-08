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
    protected abstract Task<TCacheEntity[]> QueryLiveAsync(TDbContext dbContext, TTenantKey tenantId, TKey[] keys, CancellationToken cancellationToken);

    /// <summary>
    /// create a new cache row for a key. <see cref="ITenantCacheEntity{TTenantKey}.TenantId"/> is set for you
    /// </summary>
    protected abstract TCacheEntity FromKey(TKey key);

    protected abstract Expression<Func<TCacheEntity, TKey>> KeySelector { get; }

    protected abstract void UpdateFacts(TCacheEntity existingRow, TCacheEntity incomingRow);

    protected virtual async Task MergeAsync(TDbContext dbContext, TTenantKey tenantId, TCacheEntity[] liveData, CancellationToken cancellationToken)
    {
        var keySelector = KeySelector.Compile();
        var incomingByKey = liveData
            .GroupBy(keySelector)
            .ToDictionary(g => g.Key, g => g.Last());

        var staleRows = await CacheDbSet(dbContext)
            .ForTenant(tenantId)
            .Where(row => row.IsStale)
            .ToArrayAsync(cancellationToken);

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

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> MarkStaleKeysAsync(TDbContext dbContext, TTenantKey tenantId, (bool Inserted, TKey Key)[] keys, CancellationToken cancellationToken = default)
    {
        if (!keys.Any()) return 0;

        return await Util.LogDuration(Logger, async () =>
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

            int inserted = await dbContext.SaveChangesAsync(cancellationToken);

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

                var rows = await CacheDbSet(dbContext).ForTenant(tenantId).ToArrayAsync(cancellationToken);
                foreach (var row in rows)
                {
                    if (updatedSet.Contains(keySelector(row)))
                    {
                        row.IsStale = true;
                        dbContext.Entry(row).Property(nameof(ICacheEntity.IsStale)).IsModified = true;
                    }
                }

                updated = await dbContext.SaveChangesAsync(cancellationToken);
            }

            return inserted + updated;
        });
    }

    private const int KeyChunkSize = 1000;

    /// <summary>
    /// upserts cache rows for every source row returned by <paramref name="query"/>, flagging them stale.
    /// Inserted rows have default fact values until <see cref="UpdateAsync"/> runs. The query must be scoped to the tenant
    /// </summary>
    /// <returns>number of cache rows inserted or flagged stale</returns>
    public async Task<int> MarkStaleWhereAsync<TEntity>(
        TDbContext dbContext, TTenantKey tenantId,
        IQueryable<TEntity> query, Expression<Func<TEntity, TKey>> sourceKey,
        CancellationToken cancellationToken = default) =>
        await Util.LogDuration(Logger, async () =>
        {
            var keys = await query.Select(sourceKey).Distinct().ToArrayAsync(cancellationToken);
            if (keys.Length == 0) return 0;

            var ownsTransaction = dbContext.Database.CurrentTransaction is null;
            await using var txn = ownsTransaction ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;

            int total = 0;
            foreach (var chunk in keys.Chunk(KeyChunkSize))
            {
                var inChunk = ContainsKey(chunk);

                var existingKeys = (await CacheDbSet(dbContext)
                    .AsNoTracking()
                    .ForTenant(tenantId)
                    .Where(inChunk)
                    .Select(KeySelector)
                    .ToArrayAsync(cancellationToken))
                    .ToHashSet();

                if (existingKeys.Count > 0)
                {
                    total += await CacheDbSet(dbContext)
                        .ForTenant(tenantId)
                        .Where(inChunk)
                        .ExecuteUpdateAsync(p => p.SetProperty(row => row.IsStale, true), cancellationToken);
                }

                var missing = chunk.Where(key => !existingKeys.Contains(key)).ToArray();
                if (missing.Length > 0)
                {
                    CacheDbSet(dbContext).AddRange(missing.Select(key =>
                    {
                        var row = FromKey(key);
                        row.TenantId = tenantId;
                        row.IsStale = true;
                        return row;
                    }));

                    total += await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            if (txn is not null) await txn.CommitAsync(cancellationToken);
            return total;
        });


    /// <summary>
    /// marks the source rows returned by <paramref name="query"/> stale (see <see cref="MarkStaleWhereAsync{TEntity}"/>),
    /// then refreshes all stale rows of the tenant
    /// </summary>
    /// <returns>number of cache rows refreshed</returns>
    public async Task<int> MarkAndUpdateWhereAsync<TEntity>(
        TDbContext dbContext, TTenantKey tenantId,
        IQueryable<TEntity> query, Expression<Func<TEntity, TKey>> sourceKey,
        CancellationToken cancellationToken = default)
    {
        await MarkStaleWhereAsync(dbContext, tenantId, query, sourceKey, cancellationToken);
        return await UpdateAsync(tenantId, cancellationToken);
    }

    private Expression<Func<TCacheEntity, bool>> ContainsKey(TKey[] keys)
    {
        var selector = KeySelector;
        var contains = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Contains), [typeof(TKey)],
            Expression.Constant(keys), selector.Body);
        return Expression.Lambda<Func<TCacheEntity, bool>>(contains, selector.Parameters);
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
                var keys = await CacheDbSet(db)
                    .AsNoTracking()
                    .ForTenant(tenantId)
                    .Where(e => e.IsStale)
                    .Select(KeySelector)
                    .ToArrayAsync(cancellationToken);

                var liveData = await QueryLiveAsync(db, tenantId, keys, cancellationToken);
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
