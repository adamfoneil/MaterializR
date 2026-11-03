Let's say you have calculations you need to perform in your data that are too slow to query live at scale. You'd like to cache the data, but how do you keep it updated on demand in the most efficient way? Trigger-based solutions can work, but are complex and maintenance-intensive. There are platform-specific features in SQL Server, for example, such as change tracking and change data capture. Many databases support materialized views, but they have costs, trade-offs, complications, and assumptions of their own. What if you wanted a more generic approach that leverages native EF Core functionality? That's what MaterializR is for.

MaterializR supports two kinds of cache table via abstract classes:
- [SetCache](MaterializR.Core/SetCache.cs) manages calculations based on sets of rows along one or more _dimensions_ with one or more _facts_. An example would be total sales by region and year.
- [RowCache](MaterializR.Core/RowCache.cs) manages calculations derived from a single row. An example would be the total price of line items on a sales order.

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

This is the core project goal: incremental, on-demand materialization with EF Core primitives.

### 3. How `PointsCache` aligns with the goals

[PointsCache](Demo/PointsCache.cs) is a `SetCache` example:
- **Dimensions**: `(Date, TransportId)`.
- **Facts**: `Maximum`, `Scheduled` (with computed `Available` and `PercentFree`).
- **Live query**: combines daily capacity limits with appointment points grouped by dimension.

Why it matches MaterializR:
- It demonstrates aggregated facts that would be expensive to compute repeatedly in live queries.
- It updates only dimensions touched by source changes.
- It avoids database-specific features while still maintaining near-real-time rollup data.

### 4. How `AppointmentPriceCache` aligns with the goals

[AppointmentPriceCache](Demo/AppointmentPriceCache.cs) is a `RowCache` example:
- **Key**: `AppointmentId`.
- **Fact**: `TotalPrice` (sum of `AppointmentItem` line totals).
- **Live query**: recomputes totals only for stale appointment IDs.

Why it matches MaterializR:
- It demonstrates a per-row projection cache that is cheaper to read than recalculating line-item sums every time.
- It keeps cache state synchronized when either the appointment or its items change.
- It shows the complementary pattern to `SetCache`: single-row derived values.

### 5. Run the demo

From the repo root:

`dotnet run --project Demo`

The demo uses SQLite and writes output from `PointsCacheConfiguration.UpdateAsync` so you can see the refreshed cache rows.
