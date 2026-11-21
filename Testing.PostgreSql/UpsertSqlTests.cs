using MaterializR.PostgreSql;

namespace Testing;

[TestClass]
public sealed class UpsertSqlTests
{
    [TestMethod]
    public void Statement_UsesPreparedValuesAndPreservesQuotedIdentifiersAndAssignments()
    {
        object?[][] rows =
        [
            [1, "O'Brien", true],
            [2, null, false]
        ];

        var (sql, parameters) = UpsertSql.Statement(
            "\"my.schema\".\"my\"\"table\"",
            ["\"key\"", "\"fact\"", "\"is_stale\""],
            ["\"key\""],
            "\"fact\" = EXCLUDED.\"fact\", \"is_stale\" = FALSE",
            rows);

        Assert.AreEqual(
            "INSERT INTO \"my.schema\".\"my\"\"table\" (\"key\", \"fact\", \"is_stale\") VALUES (@p0, @p1, @p2), (@p3, @p4, @p5) ON CONFLICT (\"key\") DO UPDATE SET \"fact\" = EXCLUDED.\"fact\", \"is_stale\" = FALSE",
            sql);
        CollectionAssert.AreEqual(new[] { "p0", "p1", "p2", "p3", "p4", "p5" }, parameters.ParameterNames.ToArray());
        Assert.AreEqual(1, parameters.Get<int>("p0"));
        Assert.AreEqual("O'Brien", parameters.Get<string>("p1"));
        Assert.IsTrue(parameters.Get<bool>("p2"));
        Assert.AreEqual(2, parameters.Get<int>("p3"));
        Assert.IsNull(parameters.Get<string?>("p4"));
        Assert.IsFalse(parameters.Get<bool>("p5"));
    }

    [TestMethod]
    public void Statement_RejectsEmptyRowsAndMismatchedValues()
    {
        Assert.ThrowsExactly<ArgumentException>(() => UpsertSql.Statement("\"t\"", ["\"id\""], ["\"id\""], "\"id\" = EXCLUDED.\"id\"", []));
        Assert.ThrowsExactly<ArgumentException>(() => UpsertSql.Statement("\"t\"", ["\"id\""], ["\"id\""], "\"id\" = EXCLUDED.\"id\"", [[1, 2]]));
    }
}
