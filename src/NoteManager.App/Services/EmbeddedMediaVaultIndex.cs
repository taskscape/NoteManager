using System.IO;
using NoteManager.App.Models;

namespace NoteManager.App.Services;

public sealed class EmbeddedMediaVaultIndex
{
    private readonly string _rootFolder;
    private readonly IReadOnlyDictionary<string, string> _relativePaths;
    private readonly IReadOnlyDictionary<string, string[]> _fileNames;

    private EmbeddedMediaVaultIndex(
        string rootFolder,
        IReadOnlyDictionary<string, string> relativePaths,
        IReadOnlyDictionary<string, string[]> fileNames)
    {
        _rootFolder = rootFolder;
        _relativePaths = relativePaths;
        _fileNames = fileNames;
    }

    public static EmbeddedMediaVaultIndex Create(
        string rootFolder,
        EnumerationOptions options)
    {
        var mediaPaths = Directory
            .EnumerateFiles(rootFolder, "*", options)
            .Where(path => EmbeddedMediaReference.TryGetKind(path, out _))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
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

        return new EmbeddedMediaVaultIndex(rootFolder, relativePaths, fileNames);
    }

    public EmbeddedMediaReference[] ResolveAll(
        string markdown,
        string markdownFilePath)
        => MarkdownMetadataParser
            .ParseInlineEmbeddedMediaEmbeds(markdown)
            .Select(target => new EmbeddedMediaReference(
                target,
                Resolve(target, markdownFilePath),
                GetKind(target)))
            .ToArray();

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
