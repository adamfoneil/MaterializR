using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace MaterializR.Core;

public record DimensionChangesResult(int Inserted, int Updated);

public abstract class SetCache<TDbContext, TCacheEntity, TCacheDimensions>(
    ILogger<SetCache<TDbContext, TCacheEntity, TCacheDimensions>> logger,
    IDbContextFactory<TDbContext> dbFactory) 
    where TDbContext : DbContext
    where TCacheEntity : class, ICacheEntity
    where TCacheDimensions : notnull
{
    protected readonly ILogger<SetCache<TDbContext, TCacheEntity, TCacheDimensions>> Logger = logger;

    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;

    protected abstract DbSet<TCacheEntity> RollupDbSet(TDbContext db);

    protected abstract TCacheEntity FromDimensions(TCacheDimensions dimensions);

    protected abstract TCacheDimensions ToDimensions(TCacheEntity entity);

    /// <summary>
    /// apply incoming values to an existing row, excluding key properties
    /// </summary>
    protected abstract void UpdateFacts(TCacheEntity existingRow, TCacheEntity incomingRow);

    /// <summary>
    /// query that returns the live view data for a set of dimensions
    /// </summary>
    protected abstract Task<TCacheEntity[]> QueryLiveAsync(TDbContext dbContext, TCacheDimensions[] dimensions);

    /// <summary>
    /// upsert queried live data into the cache/view table
    /// </summary>
    protected virtual async Task MergeAsync(TDbContext dbContext, TCacheEntity[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        var incomingByDimensions = data
            .GroupBy(row => ToDimensions(row))
            .ToDictionary(g => g.Key, g => g.Last());

        var dimensions = incomingByDimensions.Keys.ToArray();
        var existingRows = await ExistingRowsAsync(dbContext, dimensions);
        var existingByDimensions = existingRows.ToDictionary(row => ToDimensions(row));
        var dbSet = RollupDbSet(dbContext);

        foreach (var (dimension, incomingRow) in incomingByDimensions)
        {
            if (existingByDimensions.TryGetValue(dimension, out var existingRow))
            {
                UpdateFacts(existingRow, incomingRow);
            }
            else
            {
                dbSet.Add(incomingRow);
            }
        }

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// fetch existing rollup rows for a dimension batch so they can be marked modified
    /// </summary>
    protected virtual Task<TCacheEntity[]> ExistingRowsAsync(TDbContext dbContext, TCacheDimensions[] dimensions) =>
        RollupDbSet(dbContext).ToArrayAsync();

    /// <summary>
    /// flag dimensions as modified, inserting where they don't exist
    /// </summary>
    public async Task<DimensionChangesResult> MarkStaleDimensionsAsync(TDbContext dbContext, (bool Inserted, TCacheDimensions Dimensions)[] changes)
    {
        if (!changes.Any()) return new(0, 0);

        return await Util.TrackDuration(Logger, async () =>
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                var logDetail = string.Join(", ", changes.GroupBy(c => c.Inserted).Select(g => $"{(g.Key ? "Inserted" : "Updated")}: {string.Join(", ", g.Select(c => c.Dimensions))}"));
                Logger.LogInformation("Marking stale dimensions: {dimensions}", logDetail);
            }

            foreach (var (_, dim) in changes.Where(d => d.Inserted).Distinct())
            {
                RollupDbSet(dbContext).Add(FromDimensions(dim));
            }

            var modifiedDimensions = changes
                .Where(d => !d.Inserted)
                .Select(d => d.Dimensions)
                .Distinct()
                .ToArray();

            if (modifiedDimensions.Length > 0)
            {
                var modifiedLookup = modifiedDimensions.ToHashSet();
                var existingRows = await ExistingRowsAsync(dbContext, modifiedDimensions);

                foreach (var entity in existingRows)
                {
                    if (modifiedLookup.Contains(ToDimensions(entity)))
                    {
                        entity.IsStale = true;
                    }
                }
            }

            await dbContext.SaveChangesAsync();

            return new DimensionChangesResult(
                Inserted: changes.Count(d => d.Inserted),
                Updated: changes.Count(d => !d.Inserted));
        });
    }

    private static (bool Inserted, TCacheDimensions Dimensions)[] Added<TEntity>(ChangeTracker changeTracker, Func<TEntity, TCacheDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => (true, selector(e.Entity))).Distinct()];

    private static (bool Inserted, TCacheDimensions Dimensions)[] Modified<TEntity>(ChangeTracker changeTracker, Func<TEntity, TCacheDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => (false, selector(e.Entity))).Distinct()];

    private static (bool Inserted, TCacheDimensions Dimensions)[] ModifiedOriginal<TEntity>(ChangeTracker changeTracker, Func<TEntity, TCacheDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => (false, selector((TEntity)e.OriginalValues.ToObject()))).Distinct()];

    private static IEnumerable<(bool Inserted, TCacheDimensions Dimensions)> Deleted<TEntity>(ChangeTracker changeTracker, Func<TEntity, TCacheDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Deleted)
            .Select(e => (false, selector((TEntity)e.OriginalValues.ToObject()))).Distinct()];        

    public async Task<int> UpdateAsync()
    {
        using var db = DbFactory.CreateDbContext();
        using var txn = db.Database.BeginTransaction();

        return await Util.TrackDuration(Logger, async () =>
        {
            try
            {
                var dimensions = await RollupDbSet(db).AsNoTracking().Where(row => row.IsStale).ToArrayAsync();
                if (!dimensions.Any()) return 0;
                var dimensionKeys = dimensions.Select(row => ToDimensions(row)).ToArray();
                var liveData = await QueryLiveAsync(db, dimensionKeys);
                await MergeAsync(db, liveData);
                int result = await RollupDbSet(db).Where(row => row.IsStale).ExecuteUpdateAsync(p => p.SetProperty(row => row.IsStale, false));
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

    public (bool Inserted, TCacheDimensions Dimensions)[] GetDimensionChanges<TEntity>(ChangeTracker changeTracker, Func<TEntity, TCacheDimensions> selector) where TEntity : class
    {
        List<(bool Inserted, TCacheDimensions Dimensions)> changes = [];
        changes.AddRange(Added(changeTracker, selector));
        changes.AddRange(Modified(changeTracker, selector));
        changes.AddRange(ModifiedOriginal(changeTracker, selector));
        changes.AddRange(Deleted(changeTracker, selector));
        return [..changes];
    }
}
