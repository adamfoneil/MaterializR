using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace MaterializR.Core;

public abstract class RolldownConfiguration<TDbContext, TCacheEntity>(
    ILogger<RolldownConfiguration<TDbContext, TCacheEntity>> logger,
    IDbContextFactory<TDbContext> dbFactory)
    where TDbContext : DbContext
    where TCacheEntity : class, ICacheEntity
{
    protected readonly ILogger<RolldownConfiguration<TDbContext, TCacheEntity>> Logger = logger;

    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;

    protected abstract DbSet<TCacheEntity> RolldownDbSet(TDbContext db);

    private static (bool Inserted, TKey)[] Added<TEntity, TKey>(ChangeTracker changeTracker, Func<TEntity, TKey> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => (true, selector(e.Entity))).Distinct()];

    private static (bool Inserted, TKey)[] Modified<TEntity, TKey>(ChangeTracker changeTracker, Func<TEntity, TKey> selector) where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => (false, selector(e.Entity))).Distinct()];

    public (bool Inserted, TKey Dimensions)[] GetKeyChanges<TEntity, TKey>(TDbContext dbContext, Func<TEntity, TKey> selector) where TEntity : class
    {
        List<(bool Inserted, TKey Dimensions)> changes = [];
        changes.AddRange(Added(dbContext.ChangeTracker, selector));
        changes.AddRange(Modified(dbContext.ChangeTracker, selector));        
        return [.. changes];
    }
}
