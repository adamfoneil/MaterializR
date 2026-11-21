Let's say you have calculations you need to perform in your data that are too slow to query live at scale. You'd like to cache the data, but how do you keep it updated on demand in the simplest, most efficient way?

Trigger-based solutions can work, but are complex and maintenance-intensive. There are platform-specific features in SQL Server, for example, such as indexed views, change tracking and change data capture. Many databases support materialized views, but they have trade-offs and assumptions of their own. (For example, Postgres materialized views do not support incremental updates. SQL Server Change Data Capture requires a more expensive license, and Change Tracking does not track prior values of modified rows. SQL Server indexed views require schema binding, complicating future table changes.)

What if you wanted a more generic approach that leverages native EF Core functionality? That's what MaterializR is for.

NuGet: [EFCore.MaterializR](https://www.nuget.org/packages/EFCore.MaterializR)

MaterializR provides two cache-table abstractions and an inline cache abstraction:
- [SetCache](MaterializR.Core/SetCache.cs) manages calculations based on sets of rows along one or more _dimensions_ with one or more _facts_. This kind of cache can be called a "rollup." An example would be total sales by region and year.
- [RowCache](MaterializR.Core/RowCache.cs) manages calculations derived from a single row. An example would be the total price of line items on a sales order.
- [InlineCache](MaterializR.Core/InlineCache.cs) stores cached values directly on existing entities instead of in a separate cache table.

When you implement `SetCache` or `RowCache`, inject them in your DbContext, then override the `SaveChanges` method, calling the cache methods at a couple key points. See the walkthroughs below.

## InlineCache

Use `InlineCache` when cached values belong on an existing entity. A derived class identifies the entity set, key, and mapped stale flag, and implements the live-data refresh. `GetAsync(key)` returns `null` when the row does not exist; for a stale row, it refreshes the values and clears the stale flag in a single database update. A fresh row is returned without a refresh. Call `MarkStaleKeysAsync` with the source DbContext when the relevant source data changes.

The focused tests cover stale and fresh rows (including leaving other rows untouched), missing rows, and a failed refresh preserving the stale flag: [refresh behavior](Testing/InlineCacheTests.cs#L17), [missing row](Testing/InlineCacheTests.cs#L56), and [failed refresh](Testing/InlineCacheTests.cs#L68).

The [Testing](Testing) project runs SQLite-based tests through the demo's SQLite reference. [Testing.PostgreSql](Testing.PostgreSql) contains the PostgreSQL integration tests. Start Docker Desktop before running them; Testcontainers starts a disposable PostgreSQL container and removes it afterward. The first run downloads the PostgreSQL image if needed.

## Demo walkthrough

The [Demo](Demo) project shows how cache rows are marked stale inside `SaveChangesAsync`, then refreshed in a batch with `UpdateAsync` using a mock scheduling system.
- Relevant base entities: [Appointment](Demo/Appointment.cs), [AppointmentItem](Demo/AppointmentItem.cs). There are no particular requirements for your source-level entities that have raw data you want to aggregate.
- Cache entities: [AppointmentPriceCache](Demo/AppointmentPriceCache.cs), [PointsCache](Demo/PointsCache.cs). These should implement [ITenantCacheEntity&lt;TTenantKey&gt;](MaterializR.Core/ITenantCacheEntity.cs), which gives your cache tables the `IsStale` bool column and a `TenantId`. Include `TenantId` in the unique index of your cache tables.
- Cache examples: [PointsSetCache](Demo/Cache/PointsSetCache.cs), [AppointmentPriceRowCache](Demo/Cache/AppointmentPriceRowCache.cs). These abstract class implementations define how to query the live data. Their live queries receive the `tenantId` and must scope source data to it.

## Tenant isolation

Caches are tenant-isolated by design. `RowCache` and `SetCache` take a tenant key type parameter (`TTenantKey : notnull`), and every operation takes an explicit `tenantId`:
- `MarkStaleKeysAsync(db, tenantId, keys)` and `MarkStaleDimensionsAsync(db, tenantId, dimensions)` upsert stale rows only for that tenant. Both return `Task`; they do not report insert/update counts.
- `UpdateAsync(tenantId)` only refreshes stale rows of that tenant. There is no "all tenants" overload; loop over tenants if you need that (for example in a background job).
- Source entities need no tenant property. The tenant is never inferred from the ChangeTracker, so child entities such as `AppointmentItem` don't need one.

### 1. SaveChanges marks cache rows as stale

In [ApplicationDbContext](Demo/ApplicationDbContext.cs):
- For SetCache: `ChangeTracker.GetTouchedDimensions` maps appointment changes to `PointsCache.Dimensions`, then those dimensions are passed to `PointsCacheConfiguration.MarkStaleDimensionsAsync`.
- For RowCache: `ChangeTracker.GetTouchedKeys` maps Appointment and AppointmentItem changes to keys, then those keys are passed to `AppointmentPriceCacheConfiguration.MarkStaleKeysAsync`. A source insert does not imply a new cache row; the cache implementation upserts each identity. Default keys are ignored.

This keeps writes fast: no heavy recompute during the transaction that inserts/updates appointments. The example collects keys after saving so database-generated IDs are available, retaining the entity states until after the selector runs. `ChangeTracker.CurrentKey` reads EF's current tracked value, including generated values when changes have not yet been accepted.

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

    // Save first so generated keys are available, while retaining entity states.
    var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);

    var priceChanges = ChangeTracker.GetTouchedKeys(e => e switch
    {
        Appointment a => ChangeTracker.CurrentKey(a, x => x.Id),
        AppointmentItem ai => ChangeTracker.CurrentKey(ai, x => x.AppointmentId),
        _ => 0
    });
    ChangeTracker.AcceptAllChanges();

    await _pointsRollup.MarkStaleDimensionsAsync(this, tenantId, apptChanges);
    await _priceCache.MarkStaleKeysAsync(this, tenantId, priceChanges);

    return result;
}
```

`MarkStaleKeysAsync`, `MarkStaleDimensionsAsync`, and `MarkStaleWhereAsync` return `Task`. Their inputs describe source changes, not actual cache inserts or updates, so they do not report row counts.

## PostgreSQL

Reference [MaterializR.PostgreSql](MaterializR.PostgreSql) for abstract PostgreSQL cache bases. Both marking stale and refreshing use `INSERT ... ON CONFLICT ... DO UPDATE` and require a unique key or index on the tenant column plus the non-nullable cache key/dimension columns. Marking preserves existing facts; refresh replaces the properties selected by `FactProperties` and clears `IsStale`. Refresh writes the selected values directly from `QueryLiveAsync`, so any transformations must be applied in the live query. The fact selector must include exactly the writable facts to refresh. The bases also take the key/dimension property selector used to identify the unique constraint and use EF relational metadata and the supplied context's connection and active transaction. Refresh locks the selected stale rows until commit so concurrent markers are not lost. Non-discriminator shadow properties used in inserts require a configured constant default.

## SQLite

[MaterializR.Sqlite](MaterializR.Sqlite) contains the linked Core sources and the EF Core SQLite provider. It does not yet provide generic SQLite cache bases; subclasses must implement both upsert hooks. The demo implements them with SQLite `ON CONFLICT` statements and unique tenant/key indexes. Existing databases created before those indexes were added need the corresponding unique indexes added explicitly; `EnsureCreated` does not update an existing schema. SQLite refreshes use the default untracked read rather than PostgreSQL-style row locks; concurrent writers may cause a busy or snapshot error, which is propagated to the caller.

The SQLite and PostgreSQL projects each compile their own copy of the Core types. Reference only one of these projects or the standalone Core project in a consuming project to avoid duplicate types.

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
1. Select the tenant's stale rows in a transaction (PostgreSQL locks them until commit).
2. Query live source tables for those keys or dimensions.
3. Upsert returned facts and clear `IsStale` only for selected rows; ignore unrequested live rows.
4. Delete selected stale rows for which no live data was returned. For zero-valued rollups that should remain cached, return a zero-valued live row for each requested dimension.

`UpdateAsync` returns the number of selected rows refreshed or deleted. `GetAsync` refreshes a stale row using `UpsertFactsAsync`; if no single live row is found, it leaves the row stale and logs a warning. Orphan deletion uses the mapped primary key, which must not contain shadow properties.

Custom cache implementations must implement **both** `UpsertStaleAsync` and `UpsertFactsAsync` with atomic, tenant-scoped database upserts. The former marks rows stale without changing existing facts; the latter writes live facts and clears the stale flag. Each hook must honor the supplied context's transaction, handle an empty input, and avoid using EF's change tracker. They may share their SQL generation and execution. Provider implementations can override `LockStaleRowsAsync` for their database's locking semantics. The old `MergeAsync`, `UpdateFacts`, and `UpsertAsync` hooks have been removed.

Upserts and orphan deletions bypass EF tracking. Already-tracked cache entities are not synchronized or detached: reload them before relying on their values, and do not later save conflicting tracked inserts or modifications. Source-entity change tracking in the `SaveChangesAsync` example is separate from cache persistence.
