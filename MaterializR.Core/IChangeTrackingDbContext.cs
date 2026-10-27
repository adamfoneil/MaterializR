using Microsoft.EntityFrameworkCore;

namespace MaterializR.Core;

public interface IChangeTrackingDbContext<TKey> where TKey : struct
{
    /// <summary>
    /// similar to SQL Server CHANGE_TABLE function results, stores the actual changes in a JSON structure along with some context properties
    /// </summary>
    public DbSet<RowChange<TKey>> RowChanges { get; set; }
    /// <summary>
    /// tracks latest change so that we can efficiently query for changes since a given point in time without needing to scan the entire RowLogs table
    /// </summary>
    public DbSet<ChangeTrackingMarker> ChangeTrackingMarkers { get; set; }
}
