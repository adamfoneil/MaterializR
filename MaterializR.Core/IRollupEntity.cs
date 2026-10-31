namespace MaterializR.Core;

/// <summary>
/// describes an entity intended to store calculations (facts)
/// </summary>
public interface IRollupEntity
{
    /// <summary>
    /// indicates that underlying data has changed and this view row needs to be refreshed
    /// </summary>
    bool IsModified { get; set; }    
}
