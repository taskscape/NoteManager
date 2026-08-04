namespace NoteManager.App.Services;

/// <summary>
/// Writes a small local audit trail for application startup and folder use.
/// </summary>
public sealed class ApplicationActivityLog
{
    public const string LogFilePrefix = "Application-";
    public const int RetentionMonths = 12;

    private static readonly Lock WriteLock = new();
    private readonly string _logDirectory;

    public ApplicationActivityLog(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoteManager",
            "logs");
    }

    public bool TryWriteApplicationOpened()
        => TryWrite("Application opened.");

    public bool TryWriteFolderSelected(string folder)
        => TryWriteFolderActivity("Repository folder selected", folder);

    public bool TryWriteFolderRestoredFromPreviousSession(string folder)
        => TryWriteFolderActivity(
            "Repository folder opened from previous session",
            folder);

    private bool TryWriteFolderActivity(string activity, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return TryWrite($"{activity}: {Path.GetFullPath(folder)}");
    }

    private bool TryWrite(string message)
    {
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(_logDirectory);
                DeleteExpiredLogs();
                var path = Path.Combine(
                    _logDirectory,
                    $"{LogFilePrefix}{DateTime.Today:yyyy-MM-dd}.log");
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            // Logging must not prevent a user from opening their notes.
            return false;
        }
    }

    private void DeleteExpiredLogs()
    {
        var threshold = DateTime.Today.AddMonths(-RetentionMonths);
        foreach (var path in Directory.EnumerateFiles(
                     _logDirectory,
                     $"{LogFilePrefix}*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == "Application-yyyy-MM-dd".Length
                && DateTime.TryParseExact(
                    name[LogFilePrefix.Length..],
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var date)
                && date < threshold)
            {
                File.Delete(path);
            }
        }
    }
}
