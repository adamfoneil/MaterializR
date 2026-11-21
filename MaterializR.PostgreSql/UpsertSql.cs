using Dapper;

namespace MaterializR.PostgreSql;

internal static class UpsertSql
{
    internal static (string Sql, DynamicParameters Parameters) Statement(
        string quotedTable,
        string[] insertColumns,
        string[] conflictColumns,
        string assignments,
        IEnumerable<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var parameters = new DynamicParameters();
        var valueRows = new List<string>();
        var parameterIndex = 0;
        foreach (var row in rows)
        {
            if (row is null || row.Length != insertColumns.Length)
            {
                throw new ArgumentException("Each row must have one value per insert column.", nameof(rows));
            }

            var parameterNames = new string[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                var parameterName = $"p{parameterIndex++}";
                parameters.Add(parameterName, row[i]);
                parameterNames[i] = $"@{parameterName}";
            }
            valueRows.Add($"({string.Join(", ", parameterNames)})");
        }

        if (valueRows.Count == 0)
        {
            throw new ArgumentException("At least one row is required.", nameof(rows));
        }

        var sql = $"INSERT INTO {quotedTable} ({string.Join(", ", insertColumns)}) VALUES {string.Join(", ", valueRows)} " +
            $"ON CONFLICT ({string.Join(", ", conflictColumns)}) DO UPDATE SET {assignments}";
        return (sql, parameters);
    }
}
