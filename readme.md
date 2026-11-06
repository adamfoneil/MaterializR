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
- Relevant entities: [Appointment](Demo/Appointment.cs), [AppointmentItem](Demo/AppointmentItem.cs).
- Cache examples: [PointsCacheConfiguration.cs](https://github.com/adamfoneil/MaterializR/blob/main/Demo/AppointmentPriceCache.cs#L30), [PointsCacheConfiguration.cs](https://github.com/adamfoneil/MaterializR/blob/main/Demo/PointsCache.cs#L55)

### 1. SaveChanges marks cache rows as stale

In [ApplicationDbContext](Demo/ApplicationDbContext.cs):
- For SetCache: `ChangeTracker.GetTouchedDimensions` maps appointment changes to `PointsCache.Dimensions`, then those dimensions are passed to `PointsCacheConfiguration.MarkStaleDimensionsAsync`.
- For RowCache: `ChangeTracker.GetTouchedKeys` maps Appointment and AppointmentItem changes to appointment IDs, then those keys are passed to `AppointmentPriceCacheConfiguration.MarkStaleKeysAsync`.

This keeps writes fast: no heavy recompute during the transaction that inserts/updates appointments.

```csharp
// pass your cache configuration classes as dependencies of your DbContext
private readonly PointsCacheConfiguration _pointsRollup = pointsRollup;
private readonly AppointmentPriceCacheConfiguration _priceCache = priceCacheConfig;

public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
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
    
    await _pointsRollup.MarkStaleDimensionsAsync(this, apptChanges);
    await _priceCache.MarkStaleKeysAsync(this, priceChanges);

    return result;
}
```

### 2. UpdateAsync recomputes only stale rows

In UI where you need to present cached calculations, you'd inject the appropriate RowCache and/or SetCache instances and call their `UpdateAsync` method.

```csharp
@inject PointsCacheConfiguration PointsCache
@inject AppointmentPriceCacheConfiguration PriceCache

protected override async Task OnInitializedAsync()
{
    await PointsCache.UpdateAsync();
    await PriceCache.UpdateAsync();
}
```

Both cache types use the same cycle:
1. Read stale keys/dimensions.
2. Query live source tables for those keys only.
3. Merge facts into cache rows.
4. Clear `IsStale`.
