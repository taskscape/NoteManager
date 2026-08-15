namespace NoteManager.App.Services;

/// <summary>
/// Stores the last successfully opened Markdown folder outside the folder
/// itself, so it can be offered when NoteManager starts again.
/// </summary>
public sealed class LastOpenedFolderService
{
    public const string StateFileName = "last-folder.txt";

    private readonly string _stateFilePath;

    public LastOpenedFolderService(string? stateFilePath = null)
    {
        _stateFilePath = stateFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoteManager",
            StateFileName);
    }

    public string? ReadExistingFolder()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return null;
            }

            var folder = File.ReadAllText(_stateFilePath).Trim();
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
                ? Path.GetFullPath(folder)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return null;
        }
    }

    public bool TrySave(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        try
        {
            var fullPath = Path.GetFullPath(folder);
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
            File.WriteAllText(_stateFilePath, fullPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            return false;
        }
    }
}
