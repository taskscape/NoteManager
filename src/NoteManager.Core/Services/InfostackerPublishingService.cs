using System.Buffers;
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
    // Publishing only needs a small JSON identifier, so reject oversized bodies before parsing untrusted data.
    private const int MaximumResponseBodyBytes = 1024 * 1024;
    // The deadline covers the upload, headers, response body, and parsing rather than relying on HttpClient headers behavior.
    private static readonly TimeSpan DefaultPublishingTimeout = TimeSpan.FromMinutes(2);
    private static readonly Uri ProductionBaseUri = new("https://shr.infostacker.com/");
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly TimeSpan _publishingTimeout;

    public InfostackerPublishingService(
        HttpClient? httpClient = null,
        Uri? baseUri = null,
        TimeSpan? publishingTimeout = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _baseUri = EnsureTrailingSlash(baseUri ?? ProductionBaseUri);
        _publishingTimeout = publishingTimeout ?? DefaultPublishingTimeout;
        if (_publishingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(publishingTimeout),
                "The publishing timeout must be greater than zero.");
        }
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

        // Link caller cancellation with an operation-wide deadline so a stalled response body cannot outlive the dialog.
        using var deadlineCancellation = new CancellationTokenSource(_publishingTimeout);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);
        var operationToken = operationCancellation.Token;

        try
        {
            var source = await File
                .ReadAllTextAsync(notePath, operationToken)
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
                operationToken.ThrowIfCancellationRequested();
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
                operationToken).ConfigureAwait(false);
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
                    .ReadAsStreamAsync(operationToken)
                    .ConfigureAwait(false);
            var responseBytes = await ReadResponseBodyAsync(response, responseStream, operationToken)
                .ConfigureAwait(false);
            // Parsing receives the same operation token after the bounded read keeps synchronous parser work small.
            await using var responseBodyStream = new MemoryStream(responseBytes, writable: false);
            using var document = await JsonDocument.ParseAsync(
                responseBodyStream,
                cancellationToken: operationToken).ConfigureAwait(false);
            // The sharing protocol requires an object response; JsonElement property access throws for valid scalar JSON.
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("id", out var idElement)
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
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested)
        {
            // A deadline or HttpClient timeout is a recoverable network failure, unlike an explicit user cancellation.
            throw new InfostackerPublishingException(
                "Publishing timed out before Infostacker returned a complete response.",
                exception);
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

    private static async Task<byte[]> ReadResponseBodyAsync(
        HttpResponseMessage response,
        Stream responseStream,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBodyBytes)
        {
            throw new InfostackerPublishingException(
                "Infostacker returned a publishing response that exceeds the 1 MB limit.");
        }

        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var bytesRead = await responseStream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    return body.ToArray();
                }

                if (body.Length + bytesRead > MaximumResponseBodyBytes)
                {
                    // Count bytes while reading because a peer can omit or lie about Content-Length.
                    throw new InfostackerPublishingException(
                        "Infostacker returned a publishing response that exceeds the 1 MB limit.");
                }

                await body.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
        var originalPdfPaths = FindOriginalPdfPaths(notePath, rootPath).ToArray();
        var originalPdfPathSet = new HashSet<string>(
            originalPdfPaths,
            StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AttachmentEmbedRegex().Matches(source))
        {
            var target = ObsidianEmbedTarget.GetLiteralPath(match.Groups["target"].Value);
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

        var originalEmbeds = new List<string>();
        foreach (var originalPdfPath in originalPdfPaths)
        {
            if (resolvedPaths.Add(originalPdfPath))
            {
                attachments.Add(new PublicationAttachment(
                    Path.GetFileName(originalPdfPath),
                    originalPdfPath,
                    IsPdf: true,
                    IsImplicit: true,
                    Issue: null));
            }

            originalEmbeds.Add($"![[{ObsidianEmbedTarget.EscapeLiteralPath(Path.GetRelativePath(
                Path.GetDirectoryName(notePath)!,
                originalPdfPath))}]]");
        }

        var markdown = originalEmbeds.Count == 0
            ? source
            : $"{RemoveOriginalPdfEmbeds(source, notePath, rootPath, originalPdfPathSet).TrimEnd()}\n\n{string.Join(Environment.NewLine, originalEmbeds)}";
        return new PreparedPublication(markdown, attachments);
    }

    private static string RemoveOriginalPdfEmbeds(
        string source,
        string notePath,
        string rootPath,
        HashSet<string> originalPdfPaths)
    {
        return AttachmentEmbedRegex().Replace(source, match =>
        {
            var target = ObsidianEmbedTarget.GetLiteralPath(match.Groups["target"].Value);
            var resolvedPath = target.Length == 0
                ? null
                : ResolveExplicitAttachment(target, notePath, rootPath);
            return resolvedPath is not null && originalPdfPaths.Contains(resolvedPath)
                ? string.Empty
                : match.Value;
        });
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
