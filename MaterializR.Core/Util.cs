using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace MaterializR.Core;

internal static class Util
{
    internal static async Task<int> DeleteStaleRowsAsync<TCacheEntity, TTenantKey>(
        DbContext db, DbSet<TCacheEntity> cache, TTenantKey tenantId, TCacheEntity[] rows, CancellationToken cancellationToken)
        where TCacheEntity : class, ITenantCacheEntity<TTenantKey>
        where TTenantKey : notnull
    {
        if (rows.Length == 0) return 0;
        var entityType = db.Model.FindEntityType(typeof(TCacheEntity))
            ?? throw new InvalidOperationException($"Entity type {typeof(TCacheEntity).Name} is not in the EF model.");
        var key = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException($"Entity type {typeof(TCacheEntity).Name} needs a primary key to delete orphaned cache rows.");
        if (key.Properties.Any(property => property.IsShadowProperty()))
            throw new InvalidOperationException($"The primary key of {typeof(TCacheEntity).Name} cannot contain shadow properties when deleting orphaned cache rows.");

        var deleted = 0;
        foreach (var batch in rows.Chunk(100))
        {
            var parameter = Expression.Parameter(typeof(TCacheEntity), "row");
            Expression? anyRow = null;
            foreach (var row in batch)
            {
                Expression? allKeys = null;
                foreach (var property in key.Properties)
                {
                    var member = Expression.Call(typeof(EF), nameof(EF.Property), [property.ClrType], parameter, Expression.Constant(property.Name));
                    var value = Expression.Constant(property.GetGetter().GetClrValue(row), property.ClrType);
                    var equal = Expression.Equal(member, value);
                    allKeys = allKeys is null ? equal : Expression.AndAlso(allKeys, equal);
                }
                anyRow = anyRow is null ? allKeys : Expression.OrElse(anyRow, allKeys!);
            }
            var predicate = Expression.Lambda<Func<TCacheEntity, bool>>(anyRow!, parameter);
            deleted += await cache.ForTenant(tenantId).Where(row => row.IsStale).Where(predicate).ExecuteDeleteAsync(cancellationToken);
        }
        return deleted;
    }

    internal static async Task LogDuration<TClass>(ILogger<TClass> logger, Func<Task> action, [CallerMemberName] string? methodName = null)
    {
        var stopwatch = Stopwatch.StartNew();
        string status = "failed";

        try
        {
            await action();
            status = "succeeded";
        }
        finally
        {
            stopwatch.Stop();
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("{MethodName} {Status} in {ElapsedMilliseconds} ms", methodName, status, stopwatch.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>
    /// use in public methods only
    /// </summary>
    internal static async Task<TResult> LogDuration<TResult, TClass>(ILogger<TClass> logger, Func<Task<TResult>> action, [CallerMemberName] string? methodName = null)
    {
        var stopwatch = Stopwatch.StartNew();
        string status = "failed";

        try
        {
            var result = await action();
            status = "succeeded";
            return result;
        }
        finally
        {
            stopwatch.Stop();
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("{MethodName} {Status} in {ElapsedMilliseconds} ms", methodName, status, stopwatch.ElapsedMilliseconds);
            }
        }
    }
}
