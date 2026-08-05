namespace NoteManager.App.Services;

public sealed record ImportedPdf(
    string SourcePath,
    string DestinationPath,
    string EmbedTarget,
    bool WasCopied)
{
    public string MarkdownEmbed => $"![[{EmbedTarget}]]";
}

public static class PdfDropImportService
{
    private const int MaximumCollisionSuffix = 10_000;

    public static ImportedPdf Import(
        string sourcePath,
        string vaultRoot,
        string? markdownFilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var fullVaultRoot = Path.GetFullPath(vaultRoot);

        if (!Directory.Exists(fullVaultRoot))
        {
            throw new DirectoryNotFoundException(
                $"The open notes folder was not found: {fullVaultRoot}");
        }

        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException(
                "The dropped PDF file was not found.",
                fullSourcePath);
        }

        if (!Path.GetExtension(fullSourcePath)
            .Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Only PDF files can be embedded: {Path.GetFileName(fullSourcePath)}");
        }

        var isInsideVault = IsPathInsideFolder(fullSourcePath, fullVaultRoot);
        var destinationPath = isInsideVault
            ? fullSourcePath
            : CopyToVaultRootWithUniqueName(fullSourcePath, fullVaultRoot);
        var embedBaseFolder = ResolveEmbedBaseFolder(
            markdownFilePath,
            fullVaultRoot);
        var relativePath = Path
            .GetRelativePath(embedBaseFolder, destinationPath)
            .Replace(Path.DirectorySeparatorChar, '/');

        return new ImportedPdf(
            fullSourcePath,
            destinationPath,
            EscapeEmbedTarget(relativePath),
            WasCopied: !isInsideVault);
    }

    public static string InsertMarkdownEmbeds(
        string markdown,
        IEnumerable<string> embeds,
        int? insertionIndex = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(embeds);

        var newLine = DetectNewLine(markdown);
        var embedBlock = string.Join(
            newLine,
            embeds.Where(embed => !string.IsNullOrWhiteSpace(embed)));
        if (embedBlock.Length == 0)
        {
            return markdown;
        }

        var index = Math.Clamp(insertionIndex ?? markdown.Length, 0, markdown.Length);
        var prefix = markdown[..index];
        var suffix = markdown[index..];

        if (prefix.Length > 0 && !prefix.EndsWith('\n'))
        {
            embedBlock = $"{newLine}{newLine}{embedBlock}";
        }

        if (suffix.Length > 0
            && suffix[0] != '\r'
            && suffix[0] != '\n')
        {
            embedBlock = $"{embedBlock}{newLine}{newLine}";
        }

        return string.Concat(prefix, embedBlock, suffix);
    }

    private static string CopyToVaultRootWithUniqueName(
        string sourcePath,
        string vaultRoot)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);

        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        for (var suffix = 0; suffix <= MaximumCollisionSuffix; suffix++)
        {
            var fileName = suffix == 0
                ? $"{baseName}{extension}"
                : $"{baseName} ({suffix}){extension}";
            var candidatePath = Path.Combine(vaultRoot, fileName);
            FileStream destination;

            try
            {
                destination = new FileStream(
                    candidatePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (File.Exists(candidatePath))
            {
                continue;
            }

            try
            {
                using (destination)
                {
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                }

                return candidatePath;
            }
            catch
            {
                destination.Dispose();
                TryDeleteIncompleteCopy(candidatePath);
                throw;
            }
        }

        throw new IOException(
            $"No available filename was found for {Path.GetFileName(sourcePath)}.");
    }

    private static bool IsPathInsideFolder(string filePath, string folderPath)
    {
        var relativePath = Path.GetRelativePath(folderPath, filePath);
        return !Path.IsPathRooted(relativePath)
               && !relativePath.Equals("..", StringComparison.Ordinal)
               && !relativePath.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private static string EscapeEmbedTarget(string relativePath)
        => relativePath
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("#", "%23", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("[", "%5B", StringComparison.Ordinal)
            .Replace("]", "%5D", StringComparison.Ordinal);

    private static string DetectNewLine(string markdown)
        => markdown.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : markdown.Contains('\n')
                ? "\n"
                : Environment.NewLine;

    private static string ResolveEmbedBaseFolder(
        string? markdownFilePath,
        string vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(markdownFilePath))
        {
            return vaultRoot;
        }

        var fullMarkdownPath = Path.GetFullPath(markdownFilePath);
        if (!IsPathInsideFolder(fullMarkdownPath, vaultRoot))
        {
            throw new ArgumentException(
                "The target note is outside the open notes folder.",
                nameof(markdownFilePath));
        }

        return Path.GetDirectoryName(fullMarkdownPath) ?? vaultRoot;
    }

    private static void TryDeleteIncompleteCopy(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original copy failure.
        }
    }
}
