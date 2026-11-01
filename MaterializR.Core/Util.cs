using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MaterializR.Core;

internal static class Util
{
    /// <summary>
    /// use in public methods only
    /// </summary>
    internal static async Task<TResult> TrackDuration<TResult, TClass>(ILogger<TClass> logger, Func<Task<TResult>> action, [CallerMemberName] string? methodName = null)
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
                logger.LogInformation("Operation {MethodName} {Status} in {ElapsedMilliseconds} ms", methodName, status, stopwatch.ElapsedMilliseconds);
            }
        }
    }
}
