using System.Text.Json;

namespace NoteManager.Plugin.GitIntegration;

public sealed class GitSynchronizationOptions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 5;

    public string GitExecutablePath { get; set; } =
        OperatingSystem.IsWindows() ? "git.exe" : "git";

    public int CommandTimeoutSeconds { get; set; } = 180;

    public string CommitMessagePrefix { get; set; } = "NoteManager automatic sync";

    public static GitSynchronizationOptions LoadOrCreate(string configurationDirectory)
    {
        Directory.CreateDirectory(configurationDirectory);
        var path = Path.Combine(configurationDirectory, "settings.json");
        if (!File.Exists(path))
        {
            var defaults = new GitSynchronizationOptions();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            return defaults;
        }

        var options = JsonSerializer.Deserialize<GitSynchronizationOptions>(
                          File.ReadAllText(path),
                          JsonOptions)
                      ?? new GitSynchronizationOptions();
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (IntervalMinutes is < 1 or > 1440)
        {
            throw new InvalidDataException(
                "Git synchronization IntervalMinutes must be between 1 and 1440.");
        }

        if (CommandTimeoutSeconds is < 10 or > 3600)
        {
            throw new InvalidDataException(
                "Git synchronization CommandTimeoutSeconds must be between 10 and 3600.");
        }

        if (string.IsNullOrWhiteSpace(GitExecutablePath))
        {
            throw new InvalidDataException("GitExecutablePath cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(CommitMessagePrefix)
            || CommitMessagePrefix.Contains('\r')
            || CommitMessagePrefix.Contains('\n'))
        {
            throw new InvalidDataException(
                "CommitMessagePrefix must contain one non-empty line.");
        }
    }
}
