namespace NoteManager.Plugin.DocumentConversion;

public sealed class DocumentConversionLog(string configurationDirectory)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _logDirectory = Path.Combine(configurationDirectory, "logs");

    public async Task WriteAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_logDirectory);
            DeleteExpiredLogs();
            var path = Path.Combine(
                _logDirectory,
                $"DocumentConversion-{DateTime.Today:yyyy-MM-dd}.log");
            var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(path, line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void DeleteExpiredLogs()
    {
        var threshold = DateTime.Today.AddMonths(-1);
        foreach (var path in Directory.EnumerateFiles(
                     _logDirectory,
                     "DocumentConversion-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length == "DocumentConversion-yyyy-MM-dd".Length
                && DateTime.TryParseExact(
                    name["DocumentConversion-".Length..],
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
