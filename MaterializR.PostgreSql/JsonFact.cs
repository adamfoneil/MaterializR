namespace MaterializR.PostgreSql;

/// <summary>Provides serialized JSON for a mapped PostgreSQL jsonb column on a cache row.</summary>
/// <param name="ColumnName">The database column name, not the CLR property name.</param>
/// <param name="Serialize">Produces JSON for both stale inserts and refreshed rows. Return null for SQL NULL.</param>
public sealed record JsonFact<TCacheEntity>(string ColumnName, Func<TCacheEntity, string?> Serialize);
