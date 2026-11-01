namespace MaterializR.Core;

/// <summary>
/// describes an entity intended to store calculations or aggregates of some kind (facts)
/// </summary>
public interface ICacheEntity
{
    /// <summary>
    /// indicates that underlying/related source data has changed and this row needs to be refreshed
    /// </summary>
    bool IsStale { get; set; }
}
