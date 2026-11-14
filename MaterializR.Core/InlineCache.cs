using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace MaterializR.Core;

/// <summary>
/// defines caching strategy where cached data is saved on existing entities rather than a separate cache table
/// </summary>
public abstract class InlineCache<TDbContext, TEntity, TKey>(
    ILogger<InlineCache<TDbContext, TEntity, TKey>> logger, 
    IDbContextFactory<TDbContext> dbFactory)
    where TDbContext : DbContext
    where TEntity : class
    where TKey : notnull
{
    protected readonly ILogger<InlineCache<TDbContext, TEntity, TKey>> Logger = logger;
    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;
    protected abstract DbSet<TEntity> CacheDbSet(TDbContext db);    
    protected abstract Expression<Func<TEntity, TKey>> KeySelector { get; }    

    /// <summary>
    /// selects the writable mapped boolean stale property, for example, row => row.IsStale
    /// </summary>
    protected abstract Expression<Func<TEntity, bool>> IsStaleSelector { get; }

    /// <summary>
    /// query live data, update the untracked row's cached values, and return setters for those values
    /// without executing an update; the base class appends the stale flag and persists all values together
    /// </summary>
    protected abstract Task<Action<UpdateSettersBuilder<TEntity>>> UpdateLiveAsync(
        DbContext dbContext, TEntity row, CancellationToken cancellationToken);

    public async Task<TEntity?> GetAsync(TKey key, CancellationToken cancellationToken = default) =>
        await Util.LogDuration(Logger, async () =>
        {
            using var db = DbFactory.CreateDbContext();

            var selector = KeySelector;
            var predicate = Expression.Lambda<Func<TEntity, bool>>(
                Expression.Equal(selector.Body, Expression.Constant(key, typeof(TKey))),
                selector.Parameters);

            var row = await CacheDbSet(db).AsNoTracking().SingleOrDefaultAsync(predicate, cancellationToken);

            if (row is null) return null;

            var staleSelector = IsStaleSelector;
            if (staleSelector.Compile()(row))
            {
                var clearStale = Expression.Lambda<Action<TEntity>>(
                    Expression.Assign(staleSelector.Body, Expression.Constant(false)),
                    staleSelector.Parameters).Compile();

                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("Found stale cache row for {table} key {Key}, updating from live data", typeof(TEntity).Name, key);                    
                }

                // update the row with live data, but it's not necessary to persist as it will be done in the ExecuteUpdateAsync call next
                var updateLive = await UpdateLiveAsync(db, row, cancellationToken);

                // persists the updated values and clears the stale flag in one database round trip
                await CacheDbSet(db).Where(predicate).ExecuteUpdateAsync(p =>
                {
                    updateLive(p);
                    p.SetProperty(staleSelector, false);
                }, cancellationToken);

                // clears the stale flag on the in-memory row so that it is consistent with the database after the update
                clearStale(row);
            }

            return row;
        });
}
