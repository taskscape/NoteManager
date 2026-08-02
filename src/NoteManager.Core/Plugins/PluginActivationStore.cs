using System.Text.Json;

namespace NoteManager.Plugins;

public sealed class PluginActivationStore
{
    public const string VaultMetadataFolderName = ".note";
    public const string PluginConfigurationFolderName = "plugins";
    public const string ActivationFileName = "activated.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public IReadOnlySet<string> Load(string vaultPath)
    {
        var activationPath = GetActivationFilePath(vaultPath);
        if (!File.Exists(activationPath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var document = JsonSerializer.Deserialize<ActivationDocument>(
            File.ReadAllText(activationPath),
            JsonOptions);
        return new HashSet<string>(
            document?.EnabledPluginIds ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public void Save(string vaultPath, IEnumerable<string> enabledPluginIds)
    {
        ArgumentNullException.ThrowIfNull(enabledPluginIds);

        var activationPath = GetActivationFilePath(vaultPath);
        var directory = Path.GetDirectoryName(activationPath)!;
        Directory.CreateDirectory(directory);

        var document = new ActivationDocument
        {
            EnabledPluginIds = enabledPluginIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        var temporaryPath = Path.Combine(
            directory,
            $".{ActivationFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, activationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public string GetPluginConfigurationDirectory(
        string vaultPath,
        string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (pluginId is "." or ".."
            || pluginId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Plugin ids may contain only letters, numbers, dots, dashes, and underscores.",
                nameof(pluginId));
        }

        return Path.Combine(GetPluginRoot(vaultPath), pluginId);
    }

    public string GetActivationFilePath(string vaultPath)
        => Path.Combine(GetPluginRoot(vaultPath), ActivationFileName);

    private static string GetPluginRoot(string vaultPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        return Path.Combine(
            Path.GetFullPath(vaultPath),
            VaultMetadataFolderName,
            PluginConfigurationFolderName);
    }

    private sealed class ActivationDocument
    {
        public string[] EnabledPluginIds { get; set; } = [];
    }
}
