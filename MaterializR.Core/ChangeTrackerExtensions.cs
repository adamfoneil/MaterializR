using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MaterializR.Core;

public static class ChangeTrackerExtensions
{
    public static (bool Inserted, TKey Key)[] GetTouchedKeys<TKey>(this ChangeTracker changeTracker, Func<object, TKey> selector) =>
        [.. changeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => (e.State == EntityState.Added, selector(e.Entity)))
            .Distinct()
            .Where(val => !EqualityComparer<TKey>.Default.Equals(val.Item2, default))];

    public static (bool Inserted, TDimensions Dimensions)[] GetTouchedDimensions<TEntity, TDimensions>(
        this ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        [
            .. Added(changeTracker, selector),
            .. Modified(changeTracker, selector),
            .. ModifiedOriginal(changeTracker, selector),
            .. Deleted(changeTracker, selector)
        ];

    private static (bool Inserted, TDimensions Dimensions)[] Added<TEntity, TDimensions>(
        ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => (true, selector(e.Entity))).Distinct()];

    private static (bool Inserted, TDimensions Dimensions)[] Modified<TEntity, TDimensions>(
        ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => (false, selector(e.Entity))).Distinct()];

    private static (bool Inserted, TDimensions Dimensions)[] ModifiedOriginal<TEntity, TDimensions>(
        ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => (false, selector((TEntity)e.OriginalValues.ToObject()))).Distinct()];

    private static (bool Inserted, TDimensions Dimensions)[] Deleted<TEntity, TDimensions>(
        ChangeTracker changeTracker,
        Func<TEntity, TDimensions> selector)
        where TEntity : class =>
        [.. changeTracker.Entries<TEntity>()
            .Where(e => e.State == EntityState.Deleted)
            .Select(e => (false, selector((TEntity)e.OriginalValues.ToObject()))).Distinct()];
}
