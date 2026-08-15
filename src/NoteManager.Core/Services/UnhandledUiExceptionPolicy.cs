using System.Runtime.InteropServices;

namespace NoteManager.App.Services;

/// <summary>
/// Decides which UI-thread failures should be logged without terminating the process.
/// </summary>
public static class UnhandledUiExceptionPolicy
{
    public static bool ShouldKeepApplicationRunning(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsRecoverable(exception);
    }

    private static bool IsRecoverable(Exception exception)
    {
        if (exception is UnauthorizedAccessException or COMException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(IsRecoverable);
        }

        return exception.InnerException is not null
            && IsRecoverable(exception.InnerException);
    }
}
