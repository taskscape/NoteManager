using System.Text;

namespace NoteManager.App.Services;

/// <summary>
/// Writes a small local audit trail for application startup, folder use, and crashes.
/// </summary>
public sealed class ApplicationActivityLog
{
    public const string LogFilePrefix = "Application-";
    public const int RetentionMonths = 12;
    public const int MaxCrashTextLength = 32_768;

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

    public bool TryWriteUnhandledException(string source, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(exception);
        return TryWrite(
            $"Unhandled exception ({source}):{Environment.NewLine}{Truncate(exception.ToString())}",
            flushToDisk: true);
    }

    public bool TryWriteUnhandledException(string source, object? exceptionObject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return exceptionObject is Exception exception
            ? TryWriteUnhandledException(source, exception)
            : TryWrite(
                $"Unhandled exception ({source}): {exceptionObject ?? "(null)"}",
                flushToDisk: true);
    }

    private bool TryWriteFolderActivity(string activity, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return TryWrite($"{activity}: {Path.GetFullPath(folder)}");
    }

    private bool TryWrite(string message, bool flushToDisk = false)
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
                var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
                if (flushToDisk)
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    writer.Write(line);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                else
                {
                    File.AppendAllText(path, line);
                }
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

    private static string Truncate(string text)
        => text.Length <= MaxCrashTextLength
            ? text
            : string.Concat(
                text.AsSpan(0, MaxCrashTextLength),
                Environment.NewLine,
                "… truncated.");

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
