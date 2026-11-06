using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MaterializR.Core;

public static class RowCacheExtensions
{
    public static (bool Inserted, TKey Key)[] GetAffectedKeys<TKey>(this ChangeTracker changeTracker, Func<object, TKey> selector) =>
        [.. changeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => (e.State == EntityState.Added, selector(e.Entity)))
            .Distinct()
            .Where(val => !EqualityComparer<TKey>.Default.Equals(val.Item2, default))];
}
