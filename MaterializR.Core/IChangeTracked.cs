namespace MaterializR.Core;

public interface IChangeTracked<TKey, TDimensions> 
    where TKey : struct
    where TDimensions : notnull
{
    TKey RowId { get; }
    TDimensions Dimensions { get; }
    /// <summary>
    /// what properties are useful to know the prior and new values for (recorded only if changed)
    /// </summary>
    string[] TrackedProperties { get; }
}
