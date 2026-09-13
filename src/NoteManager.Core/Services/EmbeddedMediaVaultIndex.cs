using NoteManager.App.Models;

namespace NoteManager.App.Services;

public sealed class EmbeddedMediaVaultIndex
{
    private readonly string _rootFolder;
    // The media snapshot must retain the same file identity semantics as the active vault.
    private readonly StringComparer _pathComparer;
    // Keep every vault file so conversion-source metadata can resolve DOCX and other non-previewable documents.
    private readonly IReadOnlyList<string> _vaultFilePaths;
    private readonly IReadOnlyDictionary<string, string[]> _filesByFolder;

    private EmbeddedMediaVaultIndex(
        string rootFolder,
        StringComparer pathComparer,
        IReadOnlyList<string> vaultFilePaths,
        IReadOnlyDictionary<string, string[]> filesByFolder)
    {
        _rootFolder = rootFolder;
        _pathComparer = pathComparer;
        _vaultFilePaths = vaultFilePaths;
        _filesByFolder = filesByFolder;
    }

    public static EmbeddedMediaVaultIndex Create(
        string rootFolder,
        EnumerationOptions options)
    {
        // Preserve case-distinct folders and attachments on case-sensitive vault volumes.
        var pathComparer = FileSystemPathIdentity.GetComparer(rootFolder);
        // Capture every file once because related-document matching needs formats
        // beyond the PDF and image paths used by explicit Markdown embeds.
        var allPaths = Directory
            .EnumerateFiles(rootFolder, "*", options)
            .OrderBy(path => path, pathComparer)
            .ToArray();
        // Grouping the snapshot by folder avoids enumerating the same directory
        // once per Markdown note when a conversion output contains many notes.
        var filesByFolder = allPaths
            .GroupBy(
                path => Path.GetDirectoryName(path) ?? rootFolder,
                pathComparer)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                pathComparer);

        return new EmbeddedMediaVaultIndex(
            rootFolder,
            pathComparer,
            allPaths,
            filesByFolder);
    }

    public EmbeddedMediaReference[] ResolveAll(
        string markdown,
        string markdownFilePath)
    {
        var embeddedReferences = MarkdownMetadataParser
            .ParseInlineEmbeddedMediaEmbeds(markdown)
            .Select(target => new EmbeddedMediaReference(
                target,
                Resolve(target, markdownFilePath),
                GetKind(target)))
            .ToArray();

        // Conversion metadata survives a Markdown rename, unlike implicit basename matching, and exposes non-media sources as attachments.
        var conversionSourceReferences = MarkdownMetadataParser
            .ParseDocumentConversionSourceReferences(markdown)
            .Select(target => new EmbeddedMediaReference(
                target,
                Resolve(target, markdownFilePath),
                GetKindOrDocument(target)));

        // Related documents follow explicit Markdown embeds so authored content
        // keeps its original order, while automatic attachments remain below it.
        var resolvedPaths = new HashSet<string>(
            embeddedReferences.Select(reference => reference.ResolvedPath),
            _pathComparer);
        var relatedDocuments = conversionSourceReferences
            .Concat(ResolveRelatedDocuments(markdownFilePath))
            .Where(reference => resolvedPaths.Add(reference.ResolvedPath));

        return [.. embeddedReferences, .. relatedDocuments];
    }

    /// <summary>
    /// Finds same-folder files whose basename corresponds exactly to either the
    /// Markdown stem (note.pdf) or full Markdown name (note.md.pdf).
    /// </summary>
    private EmbeddedMediaReference[] ResolveRelatedDocuments(
        string markdownFilePath)
    {
        var fullMarkdownPath = Path.GetFullPath(markdownFilePath);
        var noteFolder = Path.GetDirectoryName(fullMarkdownPath);
        if (noteFolder is null
            || !_filesByFolder.TryGetValue(noteFolder, out var siblingPaths))
        {
            return [];
        }

        var markdownFileName = Path.GetFileName(fullMarkdownPath);
        var markdownStem = Path.GetFileNameWithoutExtension(markdownFileName);

        return siblingPaths
            .Where(path => !_pathComparer.Equals(path, fullMarkdownPath))
            // Markdown siblings are notes in their own right and must not become
            // reciprocal attachments of another Markdown note.
            .Where(path => !Path.GetExtension(path).Equals(
                ".md",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => IsCorrespondingFile(
                path,
                markdownStem,
                markdownFileName,
                _pathComparer))
            // Prefer note.ext before note.md.ext, then make the remaining order
            // deterministic across platforms and directory enumeration order.
            .OrderBy(path => Path.GetFileNameWithoutExtension(path).Equals(
                markdownStem,
                _pathComparer.Equals(StringComparer.OrdinalIgnoreCase)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
                ? 0
                : 1)
            .ThenBy(path => Path.GetFileName(path), _pathComparer)
            .Select(path => new EmbeddedMediaReference(
                Path.GetFileName(path),
                Path.GetFullPath(path),
                Path.GetExtension(path).Equals(
                    ".pdf",
                    StringComparison.OrdinalIgnoreCase)
                    ? EmbeddedMediaKind.Pdf
                    : EmbeddedMediaKind.Document))
            .ToArray();
    }

    /// <summary>
    /// Uses the candidate basename rather than a prefix comparison so similarly
    /// named files such as note-backup.pdf are not attached to note.md.
    /// </summary>
    private static bool IsCorrespondingFile(
        string candidatePath,
        string markdownStem,
        string markdownFileName,
        StringComparer pathComparer)
    {
        var candidateStem = Path.GetFileNameWithoutExtension(candidatePath);
        // Related-document names are filesystem references, so they must not bind a different-cased sibling on sensitive volumes.
        return pathComparer.Equals(candidateStem, markdownStem)
               || pathComparer.Equals(candidateStem, markdownFileName);
    }

    private static EmbeddedMediaKind GetKind(string target)
    {
        if (EmbeddedMediaReference.TryGetKind(target, out var kind))
        {
            return kind;
        }

        throw new ArgumentException(
            $"Unsupported embedded media reference: {target}",
            nameof(target));
    }

    private static EmbeddedMediaKind GetKindOrDocument(string target)
        => EmbeddedMediaReference.TryGetKind(target, out var kind)
            ? kind
            : EmbeddedMediaKind.Document;

    public string Resolve(string rawTarget, string markdownFilePath)
    {
        try
        {
            return ResolveCore(rawTarget, markdownFilePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UriFormatException)
        {
            return rawTarget.Trim();
        }
    }

    private string ResolveCore(string rawTarget, string markdownFilePath)
    {
        var target = ObsidianEmbedTarget.GetLiteralPath(rawTarget);
        var resolution = VaultAttachmentResolutionPolicy.Resolve(
            target,
            markdownFilePath,
            _rootFolder,
            _vaultFilePaths,
            _pathComparer);
        // Preserve unresolved text for the viewer while the shared policy prevents external or ambiguous files loading.
        return resolution.ResolvedPath ?? target;
    }
}
