using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Linq.Expressions;

namespace MaterializR.Core;

public static class ChangeTrackerExtensions
{
    public static (bool Inserted, TKey Key)[] GetTouchedKeys<TKey>(this ChangeTracker changeTracker, Func<object, (bool Inserted, TKey Key)> selector) =>
        [.. changeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => selector(e.Entity))
            .Distinct()
            .Where(val => !EqualityComparer<TKey>.Default.Equals(val.Key, default))];

    /// <summary>
    /// Selects a mapped property's tracked value, marking the source inserted when the entity is added.
    /// Call before accepting changes to preserve the entity state and store-generated values.
    /// </summary>
    public static (bool Inserted, TKey Key) InsertedKey<TEntity, TKey>(
        this ChangeTracker changeTracker, TEntity entity, Expression<Func<TEntity, TKey>> key)
        where TEntity : class
    {
        var entry = changeTracker.Context.Entry(entity);
        return (entry.State == EntityState.Added, entry.Property(key).CurrentValue);
    }

    /// <summary>
    /// Selects a mapped property's tracked value from a related entity, marking the source as updated.
    /// Call before accepting changes to retain store-generated values.
    /// </summary>
    public static (bool Inserted, TKey Key) RelatedKey<TEntity, TKey>(
        this ChangeTracker changeTracker, TEntity entity, Expression<Func<TEntity, TKey>> key)
        where TEntity : class =>
        (false, changeTracker.Context.Entry(entity).Property(key).CurrentValue);

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

    public static TKey[] Updated<TKey>(this (bool Inserted, TKey Key)[] items) => [.. items.Where(i => !i.Inserted).Select(i => i.Key)];
    public static TKey[] Inserted<TKey>(this (bool Inserted, TKey Key)[] items) => [.. items.Where(i => i.Inserted).Select(i => i.Key)];
    /// <summary>
    /// combines keys from inserted and updated rows
    /// </summary>
    public static TKey[] All<TKey>(this (bool Inserted, TKey Key)[] items) => [.. items.Select(i => i.Key)];

    public static TDimensions[] Updated<TEntity, TDimensions>(this (bool Inserted, TDimensions Dimensions)[] items) => [.. items.Where(i => !i.Inserted).Select(i => i.Dimensions)];
    public static TDimensions[] Inserted<TEntity, TDimensions>(this (bool Inserted, TDimensions Dimensions)[] items) => [.. items.Where(i => i.Inserted).Select(i => i.Dimensions)];
    public static TDimensions[] All<TEntity, TDimensions>(this (bool Inserted, TDimensions Dimensions)[] items) => [.. items.Select(i => i.Dimensions)];
}
