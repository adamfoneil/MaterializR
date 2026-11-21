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
    /// query that returns the live data for a set of keys. Source queries must be scoped to the tenant
    /// </summary>
    protected abstract Task<TCacheEntity[]> QueryLiveAsync(TDbContext dbContext, TTenantKey tenantId, TKey[] keys, CancellationToken cancellationToken);

    /// <summary>
    /// create a new cache row for a key. <see cref="ITenantCacheEntity{TTenantKey}.TenantId"/> is set for you
    /// </summary>
    protected abstract TCacheEntity FromKey(TKey key);

    protected abstract Expression<Func<TCacheEntity, TKey>> KeySelector { get; }

    public async Task MarkStaleKeysAsync(TDbContext dbContext, TTenantKey tenantId, TKey[] keys, CancellationToken cancellationToken = default)
    {
        if (!keys.Any()) return;

        await Util.LogDuration(Logger, async () =>
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Marking stale keys: {keys}", string.Join(", ", keys));
            }

            var rows = keys.Distinct().Select(key =>
            {
                var row = FromKey(key);
                row.TenantId = tenantId;
                row.IsStale = true;
                return row;
            }).ToArray();

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("Upserting {count} stale rows for tenant {tenantId}, keys: {keys}", rows.Length, tenantId, string.Join(", ", keys.Distinct()));
            }

            await UpsertStaleAsync(dbContext, tenantId, rows, cancellationToken);
        });
    }

    private const int KeyChunkSize = 1000;

    /// <summary>
    /// upserts cache rows for every source row returned by <paramref name="query"/>, flagging them stale.
    /// Inserted rows have default fact values until <see cref="UpdateAsync"/> runs. The query must be scoped to the tenant
    /// </summary>
    public async Task MarkStaleWhereAsync<TEntity>(
        TDbContext dbContext, TTenantKey tenantId,
        IQueryable<TEntity> query, Expression<Func<TEntity, TKey>> sourceKey,
        CancellationToken cancellationToken = default) =>
        await Util.LogDuration(Logger, async () =>
        {
            var keys = await query.Select(sourceKey).Distinct().ToArrayAsync(cancellationToken);
            if (keys.Length == 0) return;

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("Upserting {count} stale rows for tenant {tenantId}, keys: {keys}", keys.Length, tenantId, string.Join(", ", keys));
            }

            var ownsTransaction = dbContext.Database.CurrentTransaction is null;
            await using var txn = ownsTransaction ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;

            foreach (var chunk in keys.Chunk(KeyChunkSize))
            {
                var rows = chunk.Select(key =>
                {
                    var row = FromKey(key);
                    row.TenantId = tenantId;
                    row.IsStale = true;
                    return row;
                }).ToArray();
                await UpsertStaleAsync(dbContext, tenantId, rows, cancellationToken);
            }

            if (txn is not null) await txn.CommitAsync(cancellationToken);
        });


    /// <summary>
    /// marks the source rows returned by <paramref name="query"/> stale (see <see cref="MarkStaleWhereAsync{TEntity}"/>),
    /// then refreshes all stale rows of the tenant
    /// </summary>
    /// <returns>number of cache rows refreshed or deleted</returns>
    public async Task<int> MarkAndUpdateWhereAsync<TEntity>(
        TDbContext dbContext, TTenantKey tenantId,
        IQueryable<TEntity> query, Expression<Func<TEntity, TKey>> sourceKey,
        CancellationToken cancellationToken = default)
    {
        await MarkStaleWhereAsync(dbContext, tenantId, query, sourceKey, cancellationToken);
        return await UpdateAsync(tenantId, cancellationToken);
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
                var keySelector = KeySelector.Compile();
                var keys = staleRows.Select(keySelector).ToHashSet();
                if (keys.Count == 0)
                {
                    await txn.CommitAsync(cancellationToken);
                    return 0;
                }

                var liveData = (await QueryLiveAsync(db, tenantId, [.. keys], cancellationToken))
                    .Where(row => keys.Contains(keySelector(row)))
                    .GroupBy(keySelector).Select(group => group.Last()).ToArray();
                await UpsertFactsAsync(db, tenantId, liveData, cancellationToken);

                var refreshed = liveData.Select(keySelector).ToHashSet();
                var orphans = staleRows.Where(row => !refreshed.Contains(keySelector(row))).ToArray();
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

    public async Task<TCacheEntity?> GetAsync(TTenantKey tenantId, TKey key, CancellationToken cancellationToken = default) =>
        await Util.LogDuration(Logger, async () =>
        {
            using var db = DbFactory.CreateDbContext();

            var selector = KeySelector;
            var predicate = Expression.Lambda<Func<TCacheEntity, bool>>(
                Expression.Equal(selector.Body, Expression.Constant(key, typeof(TKey))),
                selector.Parameters);
            var result = await CacheDbSet(db).AsNoTracking().ForTenant(tenantId).SingleOrDefaultAsync(predicate, cancellationToken);
            if (result is null || !result.IsStale) return result;

            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("GetAsync: cache row {key} is stale, refreshing", key);
            }

            await using var txn = await db.Database.BeginTransactionAsync(cancellationToken);
            var staleRows = await LockStaleRowsAsync(db, tenantId, cancellationToken);
            var keySelector = KeySelector.Compile();
            if (staleRows.Any(row => EqualityComparer<TKey>.Default.Equals(keySelector(row), key)))
            {
                var liveData = (await QueryLiveAsync(db, tenantId, [key], cancellationToken))
                    .Where(row => EqualityComparer<TKey>.Default.Equals(keySelector(row), key)).ToArray();
                if (liveData.Length == 1)
                {
                    await UpsertFactsAsync(db, tenantId, liveData, cancellationToken);
                }
                else
                {
                    Logger.LogWarning("GetAsync: expected one live row for stale cache row {key}, found {count}", key, liveData.Length);
                }
            }
            result = await CacheDbSet(db).AsNoTracking().ForTenant(tenantId).SingleOrDefaultAsync(predicate, cancellationToken);
            await txn.CommitAsync(cancellationToken);
            return result;
        });
}
