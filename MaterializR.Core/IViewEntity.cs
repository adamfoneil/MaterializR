namespace MaterializR.Core;

/// <summary>
/// describes an entity intended to store calculations (facts)
/// </summary>
/// <typeparam name="TDimensions">represents the combination of unique properties</typeparam>
public interface IViewEntity<TDimensions>
{
    /// <summary>
    /// indicates that underlying data has changed and this view row needs to be refreshed
    /// </summary>
    bool IsModified { get; set; }
    TDimensions ToDimensions();
}
