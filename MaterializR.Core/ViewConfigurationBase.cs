using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MaterializR.Core;

public abstract class ViewConfigurationBase<TDbContext, TViewEntity, TViewDimensions>(    
    IDbContextFactory<TDbContext> dbFactory) 
    where TDbContext : DbContext
    where TViewEntity : class, IViewEntity<TViewDimensions>
{    
    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;

    protected abstract DbSet<TViewEntity> ViewEntity(TDbContext db);

    /// <summary>
    /// query that returns the live view data for a set of dimensions
    /// </summary>
    protected abstract Task<TViewEntity[]> QueryLiveAsync(TDbContext dbContext, TViewDimensions[] dimensions);

    /// <summary>
    /// upsert queried live data into the cache/view table
    /// </summary>
    protected abstract Task MergeAsync(TDbContext dbContext, TViewEntity[] data);

    /// <summary>
    /// flag dimensions as modified, inserting where they don't
    /// </summary>
    protected abstract Task MergeDimensionsAsync(TDbContext dbContext, TViewDimensions[] dimensions);

    protected TViewDimensions[] Added<TEntity>(ChangeTracker changeTracker, Func<TEntity, TViewDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => selector(e.Entity)).Distinct()];

    protected TViewDimensions[] Modified<TEntity>(ChangeTracker changeTracker, Func<TEntity, TViewDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => selector(e.Entity)).Distinct()];

    protected TViewDimensions[] ModifiedOriginal<TEntity>(ChangeTracker changeTracker, Func<TEntity, TViewDimensions> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => selector((TEntity)e.OriginalValues.ToObject())).Distinct()];

    public async Task UpdateAsync()
    {
        using var db = DbFactory.CreateDbContext();
        using var txn = db.Database.BeginTransaction();

        try
        {
            var dimensions = await ViewEntity(db).AsNoTracking().Where(row => row.IsModified).Select(row => row.ToDimensions()).ToArrayAsync();            
            var liveData = await QueryLiveAsync(db, dimensions);
            await MergeAsync(db, liveData);
            await ViewEntity(db).Where(row => row.IsModified).ExecuteUpdateAsync(p => p.SetProperty(row => row.IsModified, false));
            await txn.CommitAsync();
        }
        catch 
        {
            await txn.RollbackAsync();
            throw;
        }        
    }

    /// <summary>
    /// should be called from TDbContext.SaveChanges override to set TViewEntity.IsModified = true
    /// </summary>
    public async Task TrackModifiedAsync<TEntity>(TDbContext dbContext, ChangeTracker changeTracker, Func<TEntity, TViewDimensions> selector) where TEntity : class
    {
        List<TViewDimensions> changes = [];
        changes.AddRange(Added(changeTracker, selector));
        changes.AddRange(Modified(changeTracker, selector));
        changes.AddRange(ModifiedOriginal(changeTracker, selector));
        
        await MergeDimensionsAsync(dbContext, [..changes]);
    }
}
