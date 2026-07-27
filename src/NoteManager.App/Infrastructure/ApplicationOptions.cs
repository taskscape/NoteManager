using System.IO;
using System.Text.RegularExpressions;

namespace NoteManager.App.Infrastructure;

internal sealed record ApplicationOptions(
    string? FolderPath,
    string? AutomationPipeName,
    Uri? InfostackerBaseUri)
{
    private static readonly Regex PipeNamePattern =
        new(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    public static ApplicationOptions Parse(IReadOnlyList<string> arguments)
    {
        string? folderPath = null;
        string? automationPipeName = null;
        Uri? infostackerBaseUri = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--folder" or "-f" when index + 1 < arguments.Count:
                    folderPath = Path.GetFullPath(arguments[++index]);
                    break;

                case "--automation-pipe" when index + 1 < arguments.Count:
                    var candidate = arguments[++index];
                    if (!PipeNamePattern.IsMatch(candidate))
                    {
                        throw new ArgumentException(
                            "The automation pipe name may contain only letters, numbers, dots, underscores, and hyphens.");
                    }

                    automationPipeName = candidate;
                    break;

                case "--infostacker-base-url" when index + 1 < arguments.Count:
                    var rawBaseUrl = arguments[++index];
                    if (!Uri.TryCreate(
                            rawBaseUrl,
                            UriKind.Absolute,
                            out var candidateBaseUri)
                        || candidateBaseUri.Scheme is not ("http" or "https"))
                    {
                        throw new ArgumentException(
                            "The Infostacker base URL must be an absolute HTTP or HTTPS URL.");
                    }

                    infostackerBaseUri = candidateBaseUri;
                    break;
            }
        }

        return new ApplicationOptions(
            folderPath,
            automationPipeName,
            infostackerBaseUri);
    }
}
