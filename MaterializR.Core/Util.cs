using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MaterializR.Core;

internal static class Util
{
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
