Let's say you have calculations you need to perform in your data that are too slow to query live at scale. You'd like to cache the data, but how do you keep it updated on demand in the simplest, most efficient way?

Trigger-based solutions can work, but are complex and maintenance-intensive. There are platform-specific features in SQL Server, for example, such as indexed views, change tracking and change data capture. Many databases support materialized views, but they have trade-offs and assumptions of their own. (For example, Postgres materialized views do not support incremental updates. SQL Server Change Data Capture requires a more expensive license, and Change Tracking does not track prior values of modified rows. SQL Server indexed views require schema binding, complicating future table changes.)

What if you wanted a more generic approach that leverages native EF Core functionality? That's what MaterializR is for.

NuGet: [EFCore.MaterializR](https://www.nuget.org/packages/EFCore.MaterializR)

MaterializR supports two kinds of cache table via abstract classes:
- [SetCache](MaterializR.Core/SetCache.cs) manages calculations based on sets of rows along one or more _dimensions_ with one or more _facts_. This kind of cache can be called a "rollup." An example would be total sales by region and year.
- [RowCache](MaterializR.Core/RowCache.cs) manages calculations derived from a single row. An example would be the total price of line items on a sales order.

When you implement these abstract classes, inject them in your DbContext, then override the `SaveChanges` method, calling your SetCache and RowCache methods at a couple key points. See the walkthroughs below.

## Demo walkthrough

The [Demo](Demo) project shows how cache rows are marked stale inside `SaveChangesAsync`, then refreshed in a batch with `UpdateAsync` using a mock scheduling system.
- Relevant base entities: [Appointment](Demo/Appointment.cs), [AppointmentItem](Demo/AppointmentItem.cs). There are no particular requirements for your source-level entities that have raw data you want to aggregate.
- Cache entities: [AppointmentPriceCache](Demo/AppointmentPriceCache.cs), [PointsCache](Demo/PointsCache.cs). These should implement [ITenantCacheEntity&lt;TTenantKey&gt;](MaterializR.Core/ITenantCacheEntity.cs), which gives your cache tables the `IsStale` bool column and a `TenantId`. Include `TenantId` in the unique index of your cache tables.
- Cache examples: [PointsSetCache](Demo/Cache/PointsSetCache.cs), [AppointmentPriceRowCache](Demo/Cache/AppointmentPriceRowCache.cs). These abstract class implementations define how to query the live data. Their live queries receive the `tenantId` and must scope source data to it.

## Tenant isolation

Caches are tenant-isolated by design. `RowCache` and `SetCache` take a tenant key type parameter (`TTenantKey : notnull`), and every operation takes an explicit `tenantId`:
- `MarkStaleKeysAsync(db, tenantId, keys)` and `MarkStaleDimensionsAsync(db, tenantId, changes)` only create or flag rows of that tenant.
- `UpdateAsync(tenantId)` only refreshes stale rows of that tenant. There is no "all tenants" overload; loop over tenants if you need that (for example in a background job).
- Source entities need no tenant property. The tenant is never inferred from the ChangeTracker, so child entities such as `AppointmentItem` don't need one.
- If you override `SetCache.ExistingRowsAsync`, restrict the results to `tenantId`.

### 1. SaveChanges marks cache rows as stale

In [ApplicationDbContext](Demo/ApplicationDbContext.cs):
- For SetCache: `ChangeTracker.GetTouchedDimensions` maps appointment changes to `PointsCache.Dimensions`, then those dimensions are passed to `PointsCacheConfiguration.MarkStaleDimensionsAsync`.
- For RowCache: `ChangeTracker.GetTouchedKeys` maps Appointment and AppointmentItem changes to appointment IDs, then those keys are passed to `AppointmentPriceCacheConfiguration.MarkStaleKeysAsync`.

This keeps writes fast: no heavy recompute during the transaction that inserts/updates appointments.

The tenant is passed explicitly through a `SaveChangesAsync` overload, so there is no ambient tenant state. Callers that use the ordinary `SaveChanges`/`SaveChangesAsync` methods would skip marking cache rows stale. How you prevent that is up to you; for example, override the parameterless methods to throw `NotSupportedException`.

```csharp
// pass your cache configuration classes as dependencies of your DbContext
private readonly PointsSetCache _pointsRollup = pointsRollup;
private readonly AppointmentPriceRowCache _priceCache = priceCacheConfig;

public async Task<int> SaveChangesAsync(int tenantId, CancellationToken cancellationToken = default)
{
    // capture SetCache changes
    var apptChanges = ChangeTracker.GetTouchedDimensions<Appointment, PointsCache.Dimensions>(
        a => new PointsCache.Dimensions(a.Date, a.TransportId ?? 0));

    // capture RowCache changes
    var priceChanges = ChangeTracker.GetTouchedKeys(e => e switch
    {
        Appointment a => a.Id,
        AppointmentItem ai => ai.AppointmentId,
        _ => 0
    });

    // normal SaveChanges behavior
    var result = await base.SaveChangesAsync(cancellationToken);
    
    await _pointsRollup.MarkStaleDimensionsAsync(this, tenantId, apptChanges);
    await _priceCache.MarkStaleKeysAsync(this, tenantId, priceChanges);

    return result;
}
```

### 2. UpdateAsync recomputes only stale rows

In UI where you need to present cached calculations, you'd inject the appropriate RowCache and/or SetCache instances and call their `UpdateAsync` method with the current tenant.

```csharp
@inject PointsSetCache PointsCache
@inject AppointmentPriceRowCache PriceCache

protected override async Task OnInitializedAsync()
{
    await PointsCache.UpdateAsync(tenantId);
    await PriceCache.UpdateAsync(tenantId);
}
```

Both cache types use the same cycle:
1. Read the tenant's stale keys/dimensions.
2. Query live source tables for those keys only.
3. Merge facts into cache rows.
4. Clear `IsStale` for the tenant's rows.
