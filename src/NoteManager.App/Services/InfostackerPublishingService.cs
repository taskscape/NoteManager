using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NoteManager.App.Models;

namespace NoteManager.App.Services;

public sealed class InfostackerPublishingException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);

public sealed partial class InfostackerPublishingService
{
    private const long MaximumUploadBytes = 100L * 1024 * 1024;
    private static readonly Uri ProductionBaseUri = new("https://shr.infostacker.com/");
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public InfostackerPublishingService(
        HttpClient? httpClient = null,
        Uri? baseUri = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _baseUri = EnsureTrailingSlash(baseUri ?? ProductionBaseUri);
    }

    public async Task<string> PublishAsync(
        NoteItem note,
        string vaultRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);

        var notePath = Path.GetFullPath(note.SourceFilePath);
        var rootPath = Path.GetFullPath(vaultRoot);
        if (!note.IsMarkdownFile
            || !File.Exists(notePath)
            || !IsPathInsideRoot(notePath, rootPath))
        {
            throw new InfostackerPublishingException(
                "Only a Markdown note inside the current folder can be published.");
        }

        try
        {
            var source = await File
                .ReadAllTextAsync(notePath, cancellationToken)
                .ConfigureAwait(false);
            var title = Path.GetFileNameWithoutExtension(notePath);
            var publishedMarkdown = $"{title}\n\n{source}";
            var markdownBytes = Encoding.UTF8.GetByteCount(publishedMarkdown);
            var attachments = await Task
                .Run(
                    () => ResolveAttachments(source, rootPath),
                    cancellationToken)
                .ConfigureAwait(false);
            var totalBytes = attachments.Aggregate(
                (long)markdownBytes,
                (sum, path) => checked(sum + new FileInfo(path).Length));
            if (totalBytes > MaximumUploadBytes)
            {
                throw new InfostackerPublishingException(
                    "The note and its embedded attachments exceed Infostacker's 100 MB upload limit.");
            }

            using var form = new MultipartFormDataContent();
            form.Add(
                new StringContent(publishedMarkdown, Encoding.UTF8),
                "markdown");

            foreach (var attachmentPath in attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        attachmentPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 81920,
                        useAsync: true);
                    var content = new StreamContent(stream);
                    content.Headers.ContentType =
                        new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(content, "files", Path.GetFileName(attachmentPath));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Match the plugin: an unreadable attachment is skipped while
                    // the Markdown note itself is still published.
                }
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(_baseUri, "sharing/uploadmarkdownwithfiles"))
            {
                Content = form
            };
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                throw new InfostackerPublishingException(
                    "Infostacker rejected the upload because it exceeds the 100 MB limit.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InfostackerPublishingException(
                    $"Infostacker returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            await using var responseStream =
                await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                throw new InfostackerPublishingException(
                    "Infostacker returned an invalid publishing response.");
            }

            var id = idElement.GetString()!;
            return new Uri(_baseUri, $"sharing/{Uri.EscapeDataString(id)}").AbsoluteUri;
        }
        catch (InfostackerPublishingException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or HttpRequestException
            or JsonException
            or OverflowException)
        {
            throw new InfostackerPublishingException(
                "Failed to publish the note to Infostacker.",
                exception);
        }
    }

    private static IReadOnlyList<string> ResolveAttachments(
        string markdown,
        string rootPath)
    {
        var targets = AttachmentEmbedRegex()
            .Matches(markdown)
            .Select(match => NormalizeAttachmentTarget(
                match.Groups["target"].Value))
            .Where(target => target.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length == 0)
        {
            return [];
        }

        var files = EnumerateVaultFiles(rootPath)
            .Select(Path.GetFullPath)
            .ToArray();
        var relativePaths = files
            .GroupBy(
                path => NormalizeVaultPath(Path.GetRelativePath(rootPath, path)),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var fileNames = files
            .GroupBy(
                path => Path.GetFileName(path),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var resolved = new List<string>(targets.Length);
        foreach (var target in targets)
        {
            var normalizedTarget = NormalizeVaultPath(target);
            if (relativePaths.TryGetValue(normalizedTarget, out var exactPath))
            {
                resolved.Add(exactPath);
                continue;
            }

            var fileName = Path.GetFileName(target.Replace(
                '/',
                Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(fileName)
                && fileNames.TryGetValue(fileName, out var matches))
            {
                resolved.Add(matches[0]);
            }
        }

        return resolved
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateVaultFiles(string rootPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false
        };
        var indexPrefix = Path.Combine(rootPath, ".notes")
                          + Path.DirectorySeparatorChar;

        foreach (var path in Directory.EnumerateFiles(rootPath, "*", options))
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(
                    indexPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return fullPath;
            }
        }
    }

    private static string NormalizeAttachmentTarget(string target)
    {
        var value = Uri.UnescapeDataString(target.Trim().Trim(
            '<',
            '>',
            '"',
            '\''));
        var aliasIndex = value.IndexOf('|');
        if (aliasIndex >= 0)
        {
            value = value[..aliasIndex];
        }

        var headingIndex = value.IndexOf('#');
        if (headingIndex >= 0)
        {
            value = value[..headingIndex];
        }

        return value.Trim();
    }

    private static string NormalizeVaultPath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static bool IsPathInsideRoot(string path, string rootPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, path);
        return !Path.IsPathRooted(relativePath)
               && !relativePath.Equals("..", StringComparison.Ordinal)
               && !relativePath.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The Infostacker base URL must be absolute.",
                nameof(uri));
        }

        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri($"{uri.AbsoluteUri}/");
    }

    [GeneratedRegex(@"!\[\[(?<target>.*?)\]\]")]
    private static partial Regex AttachmentEmbedRegex();
}
