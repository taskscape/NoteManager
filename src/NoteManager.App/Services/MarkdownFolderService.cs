using System.IO;
using NoteManager.App.Models;

namespace NoteManager.App.Services;

public sealed record MarkdownFolderLoadResult(
    IReadOnlyList<NoteItem> Notes,
    int FailedFileCount,
    EmbeddedMediaVaultIndex MediaIndex);

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
        var mediaIndex = EmbeddedMediaVaultIndex.Create(normalizedFolder, options);

        var notes = new List<NoteItem>(files.Length);
        var failedFileCount = 0;

        foreach (var file in files)
        {
            try
            {
                notes.Add(CreateNote(normalizedFolder, file, mediaIndex));
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

        return new MarkdownFolderLoadResult(notes, failedFileCount, mediaIndex);
    }

    private static NoteItem CreateNote(
        string rootFolder,
        FileInfo file,
        EmbeddedMediaVaultIndex mediaIndex)
    {
        var markdown = File.ReadAllText(file.FullName);
        var tags = MarkdownMetadataParser.ParseTags(markdown);
        var mediaReferences = mediaIndex.ResolveAll(markdown, file.FullName);
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
            AttachmentDescription = mediaReferences.Length switch
            {
                0 => "Markdown note",
                1 when mediaReferences[0].Kind == EmbeddedMediaKind.Pdf => "1 embedded PDF",
                1 => "1 embedded image",
                _ => $"{mediaReferences.Length:N0} embedded attachments"
            },
            ModifiedAt = modified.ToString("dd.MM.yyyy HH:mm"),
            GeneratedFilePath = file.FullName,
            IsMarkdownFile = true,
            SourceFilePath = file.FullName,
            PlainTextContent = string.Empty,
            IsContentLoaded = false,
            EmbeddedMediaReferences = mediaReferences
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

}
