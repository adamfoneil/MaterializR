using Dapper;
using Npgsql;
using NpgsqlTypes;
using System.Data;

namespace MaterializR.PostgreSql;

internal sealed class JsonbParameter(string? value) : SqlMapper.ICustomQueryParameter
{
    public void AddParameter(IDbCommand command, string name)
    {
        var parameter = new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = (object?)value ?? DBNull.Value };
        command.Parameters.Add(parameter);
    }
}
