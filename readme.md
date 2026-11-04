Let's say you have calculations you need to perform in your data that are too slow to query live at scale. You'd like to cache the data, but how do you keep it updated on demand in the most efficient way?

Trigger-based solutions can work, but are complex and maintenance-intensive. There are platform-specific features in SQL Server, for example, such as indexed views, change tracking and change data capture. Many databases support materialized views, but they have trade-offs and assumptions of their own. (For example, Postgres materialized views do not support incremental updates. SQL Server Change Data Capture requires a more expensive license, and Change Tracking does not track prior values of modified rows. SQL Server indexed views require schema binding, complicating future table changes.)

What if you wanted a more generic approach that leverages native EF Core functionality? That's what MaterializR is for.

MaterializR supports two kinds of cache table via abstract classes:
- [SetCache](MaterializR.Core/SetCache.cs) manages calculations based on sets of rows along one or more _dimensions_ with one or more _facts_. This kind of cache can be called a "rollup." An example would be total sales by region and year.
- [RowCache](MaterializR.Core/RowCache.cs) manages calculations derived from a single row. An example would be the total price of line items on a sales order.

When you implement these abstract classes, inject them in your DbContext, then override the `SaveChanges` method, calling your SetCache and RowCache methods at a couple key points. See the walkthroughs below.

## Demo walkthrough

The [Demo](Demo) project shows how cache rows are marked stale inside `SaveChangesAsync`, then refreshed in a batch with `UpdateAsync`.

### 1. SaveChanges marks cache rows as stale

In [ApplicationDbContext](Demo/ApplicationDbContext.cs):
- Appointment changes are mapped to `PointsCache.Dimensions` and passed to `PointsCacheConfiguration.MarkStaleDimensionsAsync`.
- Appointment and AppointmentItem changes are mapped to appointment IDs and passed to `AppointmentPriceCacheConfiguration.MarkStaleKeysAsync`.

This keeps writes fast: no heavy recompute during the transaction that inserts/updates appointments.

### 2. UpdateAsync recomputes only stale rows

Both cache types use the same cycle:
1. Read stale keys/dimensions.
2. Query live source tables for those keys only.
3. Merge facts into cache rows.
4. Clear `IsStale`.
