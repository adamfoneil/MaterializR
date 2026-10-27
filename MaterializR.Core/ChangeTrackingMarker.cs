namespace MaterializR.Core;

public class ChangeTrackingMarker
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// can be a table name or any identifier used by a ChangeTracker class
    /// </summary>
    public string Name { get; set; } = default!;
    /// <summary>
    /// last queried RowChange.Id for this table, causes future queries to get only newer rows
    /// </summary>
    public long LastRowChangeId { get; set; }

    public string DisplayText => $"{Name} = {LastRowChangeId}";
}
