using System.IO;
using NoteManager.App.Models;

namespace NoteManager.App.Services;

public sealed record MarkdownFolderLoadResult(
    IReadOnlyList<NoteItem> Notes,
    int FailedFileCount);

public static class MarkdownFolderService
{
    public static MarkdownFolderLoadResult LoadFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var normalizedFolder = Path.GetFullPath(folderPath);
        if (!Directory.Exists(normalizedFolder))
        {
            throw new DirectoryNotFoundException($"Folder not found: {normalizedFolder}");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false
        };

        var files = Directory
            .EnumerateFiles(normalizedFolder, "*.md", options)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pdfIndex = PdfVaultIndex.Create(normalizedFolder, options);

        var notes = new List<NoteItem>(files.Length);
        var failedFileCount = 0;

        foreach (var file in files)
        {
            try
            {
                notes.Add(CreateNote(normalizedFolder, file, pdfIndex));
            }
            catch (IOException)
            {
                failedFileCount++;
            }
            catch (UnauthorizedAccessException)
            {
                failedFileCount++;
            }
        }

        return new MarkdownFolderLoadResult(notes, failedFileCount);
    }

    private static NoteItem CreateNote(string rootFolder, FileInfo file, PdfVaultIndex pdfIndex)
    {
        var markdown = File.ReadAllText(file.FullName);
        var tags = MarkdownMetadataParser.ParseTags(markdown);
        var pdfReferences = MarkdownMetadataParser
            .ParseInlinePdfEmbeds(markdown)
            .Select(target => pdfIndex.Resolve(target, file.FullName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var relativePath = Path.GetRelativePath(rootFolder, file.FullName);
        var relativeFolder = Path.GetDirectoryName(relativePath);
        var subtitleParts = new List<string>();

        if (tags.Length > 0)
        {
            subtitleParts.Add(string.Join(", ", tags));
        }

        if (!string.IsNullOrWhiteSpace(relativeFolder))
        {
            subtitleParts.Add(relativeFolder);
        }

        var subtitle = subtitleParts.Count > 0
            ? string.Join(" · ", subtitleParts)
            : "Markdown note";
        var modified = file.LastWriteTime;

        return new NoteItem
        {
            Title = file.Name,
            Subtitle = subtitle,
            FileName = file.Name,
            Size = FormatFileSize(file.Length),
            Date = modified.ToString("dd.MM.yyyy"),
            Notebook = new DirectoryInfo(rootFolder).Name,
            ThumbnailKind = ThumbnailKind.Markdown,
            DocumentHeading = Path.GetFileNameWithoutExtension(file.Name),
            DocumentSubheading = relativePath,
            Paragraphs = [],
            Tags = tags,
            AttachmentDescription = pdfReferences.Length switch
            {
                0 => "Markdown note",
                1 => "1 embedded PDF",
                _ => $"{pdfReferences.Length:N0} embedded PDFs"
            },
            ModifiedAt = modified.ToString("dd.MM.yyyy HH:mm"),
            GeneratedFilePath = file.FullName,
            IsMarkdownFile = true,
            SourceFilePath = file.FullName,
            PlainTextContent = string.Empty,
            IsContentLoaded = false,
            PdfReferences = pdfReferences
        };
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:N0} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:N1} KB";
        }

        return $"{bytes / (1024d * 1024d):N1} MB";
    }

    private sealed class PdfVaultIndex
    {
        private readonly string _rootFolder;
        private readonly IReadOnlyDictionary<string, string> _relativePaths;
        private readonly IReadOnlyDictionary<string, string[]> _fileNames;

        private PdfVaultIndex(
            string rootFolder,
            IReadOnlyDictionary<string, string> relativePaths,
            IReadOnlyDictionary<string, string[]> fileNames)
        {
            _rootFolder = rootFolder;
            _relativePaths = relativePaths;
            _fileNames = fileNames;
        }

        public static PdfVaultIndex Create(string rootFolder, EnumerationOptions options)
        {
            var pdfPaths = Directory
                .EnumerateFiles(rootFolder, "*.pdf", options)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var relativePaths = pdfPaths
                .GroupBy(
                    path => NormalizeLinkPath(Path.GetRelativePath(rootFolder, path)),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);

            var fileNames = pdfPaths
                .GroupBy(path => Path.GetFileName(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            return new PdfVaultIndex(rootFolder, relativePaths, fileNames);
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
}
