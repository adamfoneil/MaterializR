using Microsoft.EntityFrameworkCore;

namespace MaterializR.Core;

public class RowChange<TKey> where TKey : struct
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string TableName { get; set; } = default!;
    public TKey RowId { get; set; }
    /// <summary>
    /// json structure of modified data
    /// </summary>
    public string? Data { get; set; }
    /// <summary>
    /// tracks which consumers have consumed this change info
    /// </summary>
    public string[] ConsumersRun { get; set; } = [];
    public EntityState EntityState { get; set; }
}