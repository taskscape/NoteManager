using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NoteManager.Desktop.UiTests.Infrastructure;

internal static class UiWait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static T UntilNotNull<T>(
        Func<T?> probe,
        string description,
        TimeSpan? timeout = null)
        where T : class
    {
        T? result = null;
        Until(
            () => (result = probe()) is not null,
            description,
            timeout);
        return result!;
    }

    public static void Until(
        Func<bool> predicate,
        string description,
        TimeSpan? timeout = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var limit = timeout ?? DefaultTimeout;
        Exception? lastTransientException = null;
        while (stopwatch.Elapsed < limit)
        {
            try
            {
                if (predicate())
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or COMException)
            {
                lastTransientException = exception;
            }

            Thread.Sleep(100);
        }

        var suffix = lastTransientException is null
            ? string.Empty
            : $" Last transient error: {lastTransientException.Message}";
        throw new TimeoutException(
            $"Timed out after {limit.TotalSeconds:N0}s waiting for {description}.{suffix}");
    }
}
