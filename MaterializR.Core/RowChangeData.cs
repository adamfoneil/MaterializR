namespace MaterializR.Core;

public class RowChangeData
{
    public Dictionary<string, string> Context { get; set; } = [];
    public Dictionary<string, Change> Changes { get; set; } = [];

    public class Change
    {
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
    }

    public string? GetValue(string propertyName, Func<Change, string?> selector)
    {
        if (Changes.TryGetValue(propertyName, out Change? change)) return selector(change);
        if (Context.TryGetValue(propertyName, out string? contextValue)) return contextValue;
        return null;
    }

    public T? GetValueAs<T>(string propertyName, Func<Change, string?> selector, Func<string, T> convert)
    {
        string? value = GetValue(propertyName, selector);
        return value != null ? convert(value) : default;
    }
}
