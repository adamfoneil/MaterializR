using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace MaterializR.Core;

public abstract class ViewConfigurationBase<TDbContext, TViewEntity, TViewDimensions>(    
    IDbContextFactory<TDbContext> dbFactory) 
    where TDbContext : DbContext
    where TViewEntity : class, IViewEntity<TViewDimensions>
{    
    private readonly IDbContextFactory<TDbContext> _dbFactory = dbFactory;

    protected abstract DbSet<TViewEntity> ViewEntity { get; }

    /// <summary>
    /// query that returns the live view data for a set of dimensions
    /// </summary>
    protected abstract Task<TViewEntity[]> QueryLiveAsync(TDbContext dbContext, TViewDimensions[] dimensions, IDbContextTransaction txn);

    /// <summary>
    /// upsert queried live data into the cache/view table
    /// </summary>
    protected abstract Task MergeAsync(TDbContext dbContext, TViewEntity[] data, IDbContextTransaction txn);

    /// <summary>
    /// flag dimensions as modified, inserting where they don't
    /// </summary>
    protected abstract Task MergeDimensionsAsync(TDbContext dbContext, TViewDimensions[] dimensions);

    /// <summary>
    /// what view dimensions have changed since the last update? (where IsModified = true)
    /// </summary>
    protected abstract Task<TViewDimensions[]> QueryModifiedDimensionsAsync(TDbContext dbContext, IDbContextTransaction txn);

    /// <summary>
    /// inspect the change tracker to derive what dimensions have changed, if any
    /// </summary>
    protected abstract Task<TViewDimensions[]> TrackModifiedDimensionsAsync(TDbContext dbContext, ChangeTracker changeTracker);

    public async Task UpdateAsync()
    {
        using var db = _dbFactory.CreateDbContext();
        using var txn = db.Database.BeginTransaction();

        try
        {
            var dimensions = await ViewEntity.AsNoTracking().Where(row => row.IsModified).Select(row => row.ToDimensions()).ToArrayAsync();            
            var liveData = await QueryLiveAsync(db, dimensions, txn);
            await MergeAsync(db, liveData, txn);
            await ViewEntity.Where(row => row.IsModified).ExecuteUpdateAsync(p => p.SetProperty(row => row.IsModified, false));            

            await txn.CommitAsync();
        }
        catch 
        {
            await txn.RollbackAsync();
            throw;
        }        
    }

    /// <summary>
    /// should be called from TDbContext.SaveChanges override
    /// </summary>
    public async Task TrackChangesAsync(TDbContext dbContext, ChangeTracker changeTracker)
    {
        var dimensions = await TrackModifiedDimensionsAsync(dbContext, changeTracker);
    }
}
