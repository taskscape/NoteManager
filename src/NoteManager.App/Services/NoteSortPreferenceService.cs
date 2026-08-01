using System.Text.Json;
using NoteManager.App.Models;

namespace NoteManager.App.Services;

public static class NoteSortPreferenceService
{
    public const string SettingsFileName = "settings.json";

    private const string NotesFolderName = ".notes";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static NoteSortType Load(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var settingsPath = GetSettingsPath(folderPath);
        if (!File.Exists(settingsPath))
        {
            return NoteSortType.Updated;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<NoteSettings>(
                File.ReadAllText(settingsPath),
                SerializerOptions);
            return Enum.TryParse<NoteSortType>(
                settings?.SortType,
                ignoreCase: true,
                out var sortType)
                && Enum.IsDefined(sortType)
                    ? sortType
                    : NoteSortType.Updated;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return NoteSortType.Updated;
        }
    }

    public static void Save(string folderPath, NoteSortType sortType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Enum.IsDefined(sortType))
        {
            throw new ArgumentOutOfRangeException(nameof(sortType));
        }

        var settingsPath = GetSettingsPath(folderPath);
        var settingsFolder = Path.GetDirectoryName(settingsPath)
                             ?? throw new IOException(
                                 "The note settings folder could not be resolved.");
        Directory.CreateDirectory(settingsFolder);

        var temporaryPath = Path.Combine(
            settingsFolder,
            $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new NoteSettings(sortType.ToString()),
                    SerializerOptions));
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static string GetSettingsPath(string folderPath)
        => Path.Combine(
            Path.GetFullPath(folderPath),
            NotesFolderName,
            SettingsFileName);

    private sealed record NoteSettings(string? SortType);
}
