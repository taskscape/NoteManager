using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NoteManager.App.Models;

namespace NoteManager.App.Services;

public sealed class InfostackerPublishingException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);

public sealed record PublicationAttachment(
    string FileName,
    string? ResolvedPath,
    bool IsPdf,
    bool IsImplicit,
    string? Issue)
{
    public bool IsAvailable => Issue is null && ResolvedPath is not null;

    public string DisplayText => IsAvailable
        ? IsImplicit
            ? $"{FileName} (original PDF, added at the end of the shared note)"
            : FileName
        : $"{FileName} — {Issue}";
}

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
            var publication = PreparePublication(notePath, rootPath, source);
            EnsureReferencedPdfsAreAvailable(publication.Attachments);
            var title = Path.GetFileNameWithoutExtension(notePath);
            var publishedMarkdown = $"{title}\n\n{publication.Markdown}";
            var markdownBytes = Encoding.UTF8.GetByteCount(publishedMarkdown);
            var attachments = publication.Attachments
                .Where(attachment => attachment.IsAvailable)
                .ToArray();
            var totalBytes = attachments.Aggregate(
                (long)markdownBytes,
                (sum, attachment) => checked(sum + new FileInfo(attachment.ResolvedPath!).Length));
            if (totalBytes > MaximumUploadBytes)
            {
                throw new InfostackerPublishingException(
                    "The note and its embedded attachments exceed Infostacker's 100 MB upload limit.");
            }

            using var form = new MultipartFormDataContent();
            form.Add(
                new StringContent(publishedMarkdown, Encoding.UTF8),
                "markdown");

            foreach (var attachment in attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        attachment.ResolvedPath!,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 81920,
                        useAsync: true);
                    var content = new StreamContent(stream);
                    content.Headers.ContentType =
                        new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(content, "files", attachment.FileName);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    if (attachment.IsPdf)
                    {
                        throw new InfostackerPublishingException(
                            $"Cannot publish because the referenced PDF '{attachment.FileName}' could not be included.",
                            exception);
                    }

                    // Preserve the existing best-effort behavior for non-PDF
                    // attachments. Explicit PDFs are mandatory instead.
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

    /// <summary>
    /// Builds the attachment list shown by the share dialog from the current
    /// editor content. Publishing repeats this work against the saved file so a
    /// stale preview cannot permit an invalid PDF upload.
    /// </summary>
    public IReadOnlyList<PublicationAttachment> PreviewAttachments(
        NoteItem note,
        string vaultRoot,
        string markdown)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(markdown);

        var notePath = Path.GetFullPath(note.SourceFilePath);
        var rootPath = Path.GetFullPath(vaultRoot);
        if (!note.IsMarkdownFile
            || !File.Exists(notePath)
            || !IsPathInsideRoot(notePath, rootPath))
        {
            return [];
        }

        return PreparePublication(notePath, rootPath, markdown).Attachments;
    }

    private static PreparedPublication PreparePublication(
        string notePath,
        string rootPath,
        string source)
    {
        var attachments = new List<PublicationAttachment>();
        var resolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AttachmentEmbedRegex().Matches(source))
        {
            var target = NormalizeAttachmentTarget(match.Groups["target"].Value);
            if (target.Length == 0)
            {
                continue;
            }

            var fileName = Path.GetFileName(target.Replace('/', Path.DirectorySeparatorChar));
            var isPdf = Path.GetExtension(fileName).Equals(
                ".pdf",
                StringComparison.OrdinalIgnoreCase);
            var resolvedPath = ResolveExplicitAttachment(target, notePath, rootPath);
            if (resolvedPath is not null && resolvedPaths.Add(resolvedPath))
            {
                attachments.Add(new PublicationAttachment(
                    Path.GetFileName(resolvedPath),
                    resolvedPath,
                    isPdf,
                    IsImplicit: false,
                    Issue: null));
                continue;
            }

            if (resolvedPath is null)
            {
                attachments.Add(new PublicationAttachment(
                    fileName,
                    ResolvedPath: null,
                    IsPdf: isPdf,
                    IsImplicit: false,
                    isPdf
                        ? "referenced in Markdown but not found inside the current folder"
                        : "referenced in Markdown but not found and will not be included"));
            }
        }

        var implicitEmbeds = new List<string>();
        foreach (var originalPdfPath in FindOriginalPdfPaths(notePath, rootPath))
        {
            if (!resolvedPaths.Add(originalPdfPath))
            {
                continue;
            }

            attachments.Add(new PublicationAttachment(
                Path.GetFileName(originalPdfPath),
                originalPdfPath,
                IsPdf: true,
                IsImplicit: true,
                Issue: null));
            implicitEmbeds.Add($"![[{EscapeEmbedTarget(Path.GetRelativePath(
                Path.GetDirectoryName(notePath)!,
                originalPdfPath))}]]");
        }

        var markdown = implicitEmbeds.Count == 0
            ? source
            : $"{source.TrimEnd()}\n\n{string.Join(Environment.NewLine, implicitEmbeds)}";
        return new PreparedPublication(markdown, attachments);
    }

    private static void EnsureReferencedPdfsAreAvailable(
        IEnumerable<PublicationAttachment> attachments)
    {
        var missingPdfs = attachments
            .Where(attachment => attachment.IsPdf && !attachment.IsAvailable)
            .Select(attachment => attachment.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingPdfs.Length > 0)
        {
            throw new InfostackerPublishingException(
                "Cannot publish because these PDFs are referenced in Markdown but could not be found and included: "
                + string.Join(", ", missingPdfs));
        }
    }

    private static string? ResolveExplicitAttachment(
        string target,
        string notePath,
        string rootPath)
    {
        var windowsTarget = target.Replace('/', Path.DirectorySeparatorChar);
        var noteFolder = Path.GetDirectoryName(notePath)!;
        var isExplicitRelative = target.StartsWith("./", StringComparison.Ordinal)
                                 || target.StartsWith("../", StringComparison.Ordinal)
                                 || target.StartsWith(@".\", StringComparison.Ordinal)
                                 || target.StartsWith(@"..\", StringComparison.Ordinal);
        var hasFolder = target.Contains('/') || target.Contains('\\');
        var candidate = isExplicitRelative
            ? Path.GetFullPath(Path.Combine(noteFolder, windowsTarget))
            : hasFolder
                ? Path.GetFullPath(Path.Combine(rootPath, windowsTarget))
                : Path.GetFullPath(Path.Combine(noteFolder, windowsTarget));

        if (IsPathInsideRoot(candidate, rootPath) && File.Exists(candidate))
        {
            return candidate;
        }

        // A bare filename can deliberately refer to a file at the vault root;
        // do not search every matching basename because that can select a PDF
        // from an unrelated folder.
        if (!hasFolder)
        {
            candidate = Path.GetFullPath(Path.Combine(rootPath, windowsTarget));
            if (IsPathInsideRoot(candidate, rootPath) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> FindOriginalPdfPaths(string notePath, string rootPath)
    {
        var candidates = new[]
        {
            Path.ChangeExtension(notePath, ".pdf"),
            $"{notePath}.pdf"
        };

        return candidates
            .Select(Path.GetFullPath)
            .Where(path => IsPathInsideRoot(path, rootPath) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
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

    private static string EscapeEmbedTarget(string relativePath) => relativePath
        .Replace(Path.DirectorySeparatorChar, '/')
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("#", "%23", StringComparison.Ordinal)
        .Replace("|", "%7C", StringComparison.Ordinal)
        .Replace("[", "%5B", StringComparison.Ordinal)
        .Replace("]", "%5D", StringComparison.Ordinal);

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

    private sealed record PreparedPublication(
        string Markdown,
        IReadOnlyList<PublicationAttachment> Attachments);
}
