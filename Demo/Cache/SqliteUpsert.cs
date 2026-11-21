using Microsoft.EntityFrameworkCore;

namespace Demo.Cache;

internal static class SqliteUpsert
{
    internal static async Task ExecuteAsync<TRow>(
        ApplicationDbContext db, TRow[] rows, Func<TRow, FormattableString> command, CancellationToken cancellationToken)
    {
        if (rows.Length == 0) return;

        var ownsTransaction = rows.Length > 1 && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        foreach (var row in rows)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(command(row), cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }
}
