using NoteManager.App.Models;

namespace NoteManager.App.Services;

public sealed class EmbeddedMediaVaultIndex
{
    private readonly string _rootFolder;
    private readonly IReadOnlyDictionary<string, string> _relativePaths;
    private readonly IReadOnlyDictionary<string, string[]> _fileNames;
    private readonly IReadOnlyDictionary<string, string[]> _filesByFolder;

    private EmbeddedMediaVaultIndex(
        string rootFolder,
        IReadOnlyDictionary<string, string> relativePaths,
        IReadOnlyDictionary<string, string[]> fileNames,
        IReadOnlyDictionary<string, string[]> filesByFolder)
    {
        _rootFolder = rootFolder;
        _relativePaths = relativePaths;
        _fileNames = fileNames;
        _filesByFolder = filesByFolder;
    }

    public static EmbeddedMediaVaultIndex Create(
        string rootFolder,
        EnumerationOptions options)
    {
        // Capture every file once because related-document matching needs formats
        // beyond the PDF and image paths used by explicit Markdown embeds.
        var allPaths = Directory
            .EnumerateFiles(rootFolder, "*", options)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mediaPaths = allPaths
            .Where(path => EmbeddedMediaReference.TryGetKind(path, out _))
            .ToArray();

        var relativePaths = mediaPaths
            .GroupBy(
                path => NormalizeLinkPath(Path.GetRelativePath(rootFolder, path)),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var fileNames = mediaPaths
            .GroupBy(path => Path.GetFileName(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        // Grouping the snapshot by folder avoids enumerating the same directory
        // once per Markdown note when a conversion output contains many notes.
        var filesByFolder = allPaths
            .GroupBy(
                path => Path.GetDirectoryName(path) ?? rootFolder,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return new EmbeddedMediaVaultIndex(
            rootFolder,
            relativePaths,
            fileNames,
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

        // Related documents follow explicit Markdown embeds so authored content
        // keeps its original order, while automatic attachments remain below it.
        var resolvedPaths = new HashSet<string>(
            embeddedReferences.Select(reference => reference.ResolvedPath),
            StringComparer.OrdinalIgnoreCase);
        var relatedDocuments = ResolveRelatedDocuments(markdownFilePath)
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
            .Where(path => !path.Equals(
                fullMarkdownPath,
                StringComparison.OrdinalIgnoreCase))
            // Markdown siblings are notes in their own right and must not become
            // reciprocal attachments of another Markdown note.
            .Where(path => !Path.GetExtension(path).Equals(
                ".md",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => IsCorrespondingFile(
                path,
                markdownStem,
                markdownFileName))
            // Prefer note.ext before note.md.ext, then make the remaining order
            // deterministic across platforms and directory enumeration order.
            .OrderBy(path => Path.GetFileNameWithoutExtension(path).Equals(
                markdownStem,
                StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
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
        string markdownFileName)
    {
        var candidateStem = Path.GetFileNameWithoutExtension(candidatePath);
        return candidateStem.Equals(markdownStem, StringComparison.OrdinalIgnoreCase)
               || candidateStem.Equals(markdownFileName, StringComparison.OrdinalIgnoreCase);
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
        var target = Uri.UnescapeDataString(rawTarget.Trim().Trim('<', '>', '"', '\''));
        var windowsTarget = target.Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(windowsTarget))
        {
            return Path.GetFullPath(windowsTarget);
        }

        var noteFolder = Path.GetDirectoryName(markdownFilePath) ?? _rootFolder;
        var isExplicitRelative = target.StartsWith("./", StringComparison.Ordinal)
                                 || target.StartsWith("../", StringComparison.Ordinal)
                                 || target.StartsWith(@".\", StringComparison.Ordinal)
                                 || target.StartsWith(@"..\", StringComparison.Ordinal);
        var containsFolder = target.Contains('/') || target.Contains('\\');
        var noteRelative = Path.GetFullPath(Path.Combine(noteFolder, windowsTarget));
        var vaultRelative = Path.GetFullPath(Path.Combine(_rootFolder, windowsTarget));

        if (isExplicitRelative)
        {
            return noteRelative;
        }

        if (!containsFolder && File.Exists(noteRelative))
        {
            return noteRelative;
        }

        var normalizedTarget = NormalizeLinkPath(target);
        if (_relativePaths.TryGetValue(normalizedTarget, out var indexedVaultPath))
        {
            return indexedVaultPath;
        }

        if (File.Exists(vaultRelative))
        {
            return vaultRelative;
        }

        var fileName = Path.GetFileName(windowsTarget);
        if (_fileNames.TryGetValue(fileName, out var matches))
        {
            return matches
                .OrderBy(path => RelativeDistance(noteFolder, Path.GetDirectoryName(path) ?? _rootFolder))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        return containsFolder ? vaultRelative : noteRelative;
    }

    private static string NormalizeLinkPath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static int RelativeDistance(string fromFolder, string toFolder)
    {
        var relative = Path.GetRelativePath(fromFolder, toFolder);
        return relative
            .Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }
}
