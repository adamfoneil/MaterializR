namespace MaterializR.Core;

/// <summary>
/// a cache entity whose rows belong to a single tenant.
/// Cache rows are always read and updated within one tenant, so the tenant key must be part of the unique index of the cache table
/// </summary>
public interface ITenantCacheEntity<TTenantKey> : ICacheEntity where TTenantKey : notnull
{
    TTenantKey TenantId { get; set; }
}

internal static class TenantQuery
{
    public static IQueryable<TEntity> ForTenant<TEntity, TTenantKey>(this IQueryable<TEntity> query, TTenantKey tenantId)
        where TEntity : class, ITenantCacheEntity<TTenantKey>
        where TTenantKey : notnull =>
        query.Where(row => row.TenantId.Equals(tenantId));
}
