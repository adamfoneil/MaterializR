using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Linq.Expressions;

namespace MaterializR.Core;

public static class ChangeTrackerExtensions
{
    public static TKey[] GetTouchedKeys<TKey>(this ChangeTracker changeTracker, Func<object, TKey> selector) =>
        [.. changeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => selector(e.Entity))
            .Where(key => !EqualityComparer<TKey>.Default.Equals(key, default))
            .Distinct()];

    /// <summary>
    /// Reads a mapped property's current tracked value, including store-generated values when changes have not been accepted.
    /// </summary>
    public static TKey CurrentKey<TEntity, TKey>(
        this ChangeTracker changeTracker, TEntity entity, Expression<Func<TEntity, TKey>> key)
        where TEntity : class =>
        changeTracker.Context.Entry(entity).Property(key).CurrentValue;

    public static TDimensions[] GetTouchedDimensions<TEntity, TDimensions>(
        this ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        Added(changeTracker, selector)
            .Concat(Modified(changeTracker, selector))
            .Concat(ModifiedOriginal(changeTracker, selector))
            .Concat(Deleted(changeTracker, selector))
            .Distinct()
            .ToArray();

    private static TDimensions[] Added<TEntity, TDimensions>(
        ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => selector(e.Entity))];

    private static TDimensions[] Modified<TEntity, TDimensions>(
        ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => selector(e.Entity))];

    private static TDimensions[] ModifiedOriginal<TEntity, TDimensions>(
        ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => selector((TEntity)e.OriginalValues.ToObject()))];

    private static TDimensions[] Deleted<TEntity, TDimensions>(
        ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Deleted)
            .Select(e => selector((TEntity)e.OriginalValues.ToObject()))];
}
