using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MaterializR.Core;

public abstract class RollupConfiguration<TDbContext, TViewEntity, TViewDimensions>(    
    IDbContextFactory<TDbContext> dbFactory) 
    where TDbContext : DbContext
    where TViewEntity : class, IRollupEntity
    where TViewDimensions : notnull
{    
    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;

    protected abstract DbSet<TViewEntity> RollupEntityDbSet(TDbContext db);

    protected abstract TViewEntity FromDimensions(TViewDimensions dimensions);

    protected abstract TViewDimensions ToDimensions(TViewEntity entity);

    /// <summary>
    /// query that returns the live view data for a set of dimensions
    /// </summary>
    protected abstract Task<TViewEntity[]> QueryLiveAsync(TDbContext dbContext, TViewDimensions[] dimensions);

    /// <summary>
    /// upsert queried live data into the cache/view table
    /// </summary>
    protected virtual async Task MergeAsync(TDbContext dbContext, TViewEntity[] data)
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
        var dbSet = RollupEntityDbSet(dbContext);

        foreach (var (dimension, incomingRow) in incomingByDimensions)
        {
            if (existingByDimensions.TryGetValue(dimension, out var existingRow))
            {
                dbContext.Entry(existingRow).CurrentValues.SetValues(incomingRow);
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
    protected virtual Task<TViewEntity[]> ExistingRowsAsync(TDbContext dbContext, TViewDimensions[] dimensions) =>
        RollupEntityDbSet(dbContext).ToArrayAsync();

    /// <summary>
    /// flag dimensions as modified, inserting where they don't exist
    /// </summary>
    public async Task TrackDimensionChangesAsync(TDbContext dbContext, (bool Inserted, TViewDimensions Dimensions)[] changes)
    {
        if (!changes.Any()) return;

        foreach (var (_, dim) in changes.Where(d => d.Inserted).Distinct())
        {
            RollupEntityDbSet(dbContext).Add(FromDimensions(dim));
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
                    entity.IsModified = true;
                }
            }
        }
        
        await dbContext.SaveChangesAsync();
    }

    protected (bool Inserted, TViewDimensions Dimensions)[] Added<TEntity>(ChangeTracker changeTracker, Func<TEntity, TViewDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => (true, selector(e.Entity))).Distinct()];

    protected (bool Inserted, TViewDimensions Dimensions)[] Modified<TEntity>(ChangeTracker changeTracker, Func<TEntity, TViewDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => (false, selector(e.Entity))).Distinct()];

    protected (bool Inserted, TViewDimensions Dimensions)[] ModifiedOriginal<TEntity>(ChangeTracker changeTracker, Func<TEntity, TViewDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => (false, selector((TEntity)e.OriginalValues.ToObject()))).Distinct()];

    public async Task UpdateAsync()
    {
        using var db = DbFactory.CreateDbContext();
        using var txn = db.Database.BeginTransaction();

        try
        {
            var dimensions = await RollupEntityDbSet(db).AsNoTracking().Where(row => row.IsModified).Select(row => ToDimensions(row)).ToArrayAsync();            
            var liveData = await QueryLiveAsync(db, dimensions);
            await MergeAsync(db, liveData);
            await RollupEntityDbSet(db).Where(row => row.IsModified).ExecuteUpdateAsync(p => p.SetProperty(row => row.IsModified, false));
            await txn.CommitAsync();
        }
        catch 
        {
            await txn.RollbackAsync();
            throw;
        }        
    }

    public (bool Inserted, TViewDimensions Dimensions)[] GetDimensionChanges<TEntity>(TDbContext dbContext) where TEntity : class
    {
        List<(bool Inserted, TViewDimensions Dimensions)> changes = [];
        changes.AddRange(Added<TViewEntity>(dbContext.ChangeTracker, ToDimensions));
        changes.AddRange(Modified<TViewEntity>(dbContext.ChangeTracker, ToDimensions));
        changes.AddRange(ModifiedOriginal<TViewEntity>(dbContext.ChangeTracker, ToDimensions));
        return [..changes];
    }
}
