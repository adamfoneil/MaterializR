MaterializR is an EF Core-oriented framework for maintaining materialized view tables through dimension-based partial refresh.

## Status

Current repository state is early-stage:

- Core contracts and base classes exist in `MaterializR.Core`.
- `Demo` contains a view model/configuration skeleton.
- End-to-end refresh orchestration is not fully implemented yet.

## Current API surface (source of truth)

The active API in this repo is:

- `IViewEntity<TDimensions>`
  - `bool IsModified { get; set; }`
  - `TDimensions ToDimensions()`
- `IChangeTracked<TKey, TDimensions>`
  - `TKey RowId { get; }`
  - `TDimensions Dimensions { get; }`
  - `string[] TrackedProperties { get; }`
- `IChangeTrackingDbContext<TKey>`
  - `DbSet<RowChange<TKey>> RowChanges`
  - `DbSet<ChangeTrackingMarker> ChangeTrackingMarkers`
- `ViewConfigurationBase<TDbContext, TViewEntity, TViewDimensions>`
  - `UpdateAsync()`
  - `TrackChangesAsync(TDbContext dbContext, ChangeTracker changeTracker)`

Supporting types:

- `RowChange<TKey>`: per-row change log metadata and optional serialized payload.
- `RowChangeData`: helper type for context/old/new values.
- `ChangeTrackingMarker`: consumer marker for incremental read position.

## Conceptual model

1. A view table row implements `IViewEntity<TDimensions>`.
2. `TDimensions` represents the unique grouping key for a cached view row.
3. Changed dimensions are marked as modified.
4. `UpdateAsync()` queries live data for modified dimensions and merges results back into the cache table.

## Implementing a view

Inherit from `ViewConfigurationBase<TDbContext, TViewEntity, TViewDimensions>` and implement:

- `QueryLiveAsync(...)`
- `MergeAsync(...)`
- `MergeDimensionsAsync(...)`
- `QueryModifiedDimensionsAsync(...)`
- `TrackModifiedDimensionsAsync(...)`

`Demo/Points.cs` shows the intended shape, but methods are currently placeholders.

## Important notes

- Older names from previous iterations (`IView`, `ITracked`, `IViewConfiguration`, `ContextProperties`, `GetSourceView`) are not part of the current codebase.
- Automatic full wiring from `SaveChanges` to durable dimension marking is not complete in the current base implementation.
- Treat this package as a framework foundation under active design.

## Recommended next milestones

1. Complete `TrackChangesAsync` flow so tracked dimensions are persisted via `MergeDimensionsAsync`.
2. Finalize one source of truth for pending dimensions (`IsModified` rows vs marker-driven query).
3. Add runnable demo workflow (seed -> mutate source -> refresh -> verify view rows).
4. Add tests for dimension detection, merge semantics, and concurrency behavior.
