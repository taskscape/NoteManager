using System.Text;
using System.Text.Json;
using NoteManager.App.Services;
using NoteManager.Plugins;

namespace NoteManager.Plugin.DocumentConversion;

public sealed record DocumentConversionResult(
    bool Succeeded,
    bool Skipped,
    string Message,
    int Total = 0,
    int Converted = 0,
    int ExistingOrSkipped = 0,
    int Failures = 0);

public sealed class DocumentConversionService(
    IDoc2MdProcessRunner runner,
    DocumentConversionLog log,
    DocumentConversionOptions options)
{
    private static readonly HashSet<string> SupportedExtensions = new(
        [".pdf", ".doc", ".docx", ".docm", ".xlsx", ".xls", ".xlsm",
         ".pptx", ".ppt", ".pptm", ".rtf", ".odt", ".ods", ".odp",
         ".txt", ".text", ".csv", ".html", ".htm", ".epub"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> LegacyExtensions = new(
        [".doc", ".docm", ".rtf", ".odt", ".xls", ".xlsm", ".ods",
         ".ppt", ".pptm", ".odp"],
        StringComparer.OrdinalIgnoreCase);

    // Explicit byte-order marks allow text copied from Windows applications to retain its original characters.
    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LittleEndianByteOrderMark = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianByteOrderMark = [0xFE, 0xFF];
    private static readonly byte[] Utf32LittleEndianByteOrderMark = [0xFF, 0xFE, 0x00, 0x00];
    private static readonly byte[] Utf32BigEndianByteOrderMark = [0x00, 0x00, 0xFE, 0xFF];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8WithByteOrderMark = new(true);
    private static readonly UnicodeEncoding Utf16LittleEndian = new(false, false, true);
    private static readonly UnicodeEncoding Utf16BigEndian = new(true, false, true);
    private static readonly UTF32Encoding Utf32LittleEndian = new(false, false, true);
    private static readonly UTF32Encoding Utf32BigEndian = new(true, false, true);

    public async Task<DocumentConversionResult> ConvertPendingAsync(
        PluginHostContext context,
        CancellationToken cancellationToken = default)
    {
        FileStream conversionLock;
        try
        {
            Directory.CreateDirectory(context.ConfigurationDirectory);
            conversionLock = new FileStream(
                Path.Combine(context.ConfigurationDirectory, "conversion.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            const string message =
                "Another NoteManager process is converting documents in this notes folder.";
            context.ReportStatus(message);
            return new DocumentConversionResult(false, true, message);
        }

        await using var heldConversionLock = conversionLock;
        var pendingDocuments = FindPendingDocuments(
            context.VaultPath,
            options.Recursive);
        context.ReportStatus("Checking for documents that need Markdown counterparts…");
        await log.WriteAsync(
            $"Starting newest-first conversion of {pendingDocuments.Count:N0} document(s).",
            CancellationToken.None);

        var converted = 0;
        var skipped = 0;
        var failures = 0;
        for (var index = 0; index < pendingDocuments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = pendingDocuments[index];
            if (File.Exists(document.OutputPath))
            {
                skipped++;
                continue;
            }

            var relativePath = Path.GetRelativePath(
                context.VaultPath,
                document.InputPath);
            context.ReportStatus(
                $"Converting document {index + 1:N0} of {pendingDocuments.Count:N0}: {relativePath}");
            var stagingOutput = CreateStagingOutput(document.OutputPath);
            try
            {
                string conversionInputPath;
                try
                {
                    conversionInputPath = PrepareTextInput(document, stagingOutput.DirectoryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    failures++;
                    await LogItemFailureAsync(relativePath, exception.Message);
                    continue;
                }

                var process = await runner.ConvertFileAsync(
                    conversionInputPath,
                    stagingOutput.OutputPath,
                    cancellationToken);

                if (process.WasCancelled)
                {
                    var message =
                        $"Document conversion was cancelled after {converted:N0} successful conversion(s).";
                    await LogAndReportAsync(context, message, CancellationToken.None);
                    return new DocumentConversionResult(
                        false,
                        true,
                        message,
                        pendingDocuments.Count,
                        converted,
                        skipped,
                        failures);
                }

                if (process.TimedOut)
                {
                    failures++;
                    await LogItemFailureAsync(
                        relativePath,
                        $"exceeded the {options.CommandTimeoutMinutes:N0}-minute timeout");
                    continue;
                }

                if (!TryReadItemResult(process.StandardOutput, out var item))
                {
                    failures++;
                    var detail = process.Succeeded
                        ? $"returned an invalid JSON result: {Bound(process.StandardOutput)}"
                        : $"failed with exit code {process.ExitCode}: {Bound(process.StandardError)}";
                    await LogItemFailureAsync(relativePath, detail);
                    continue;
                }

                if (item.Skipped)
                {
                    skipped++;
                    continue;
                }

                if (IsPasswordProtectedPdf(document, item))
                {
                    skipped++;
                    // DOC2MD keeps its source-side .ex diagnostic, so encryption remains visible without inflating errors.
                    await LogAndReportAsync(
                        context,
                        $"Document conversion skipped for '{relativePath}': PDF is password protected; DOC2MD diagnostics were preserved.",
                        CancellationToken.None);
                    continue;
                }

                if (process.Succeeded
                    && item.Succeeded
                    && File.Exists(stagingOutput.OutputPath))
                {
                    try
                    {
                        // Complete post-processing while the output is private so failures cannot affect a concurrent note.
                        AppendConversionSourceReference(document, stagingOutput.OutputPath);
                        AppendOriginalPdfEmbed(document, stagingOutput.OutputPath);
                        if (!TryPublishStagedDocument(stagingOutput.OutputPath, document.OutputPath))
                        {
                            failures++;
                            await LogItemFailureAsync(
                                relativePath,
                                "the Markdown output path was created by another writer during conversion");
                            continue;
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        failures++;
                        await LogItemFailureAsync(relativePath, exception.Message);
                        continue;
                    }

                    converted++;
                    continue;
                }

                failures++;
                var failureDetail = string.IsNullOrWhiteSpace(process.StandardError)
                    ? "DOC2MD reported that the document was not converted"
                    : Bound(process.StandardError);
                await LogItemFailureAsync(relativePath, failureDetail);
            }
            finally
            {
                // Each operation cleans only its own private staging directory, never a shared output location.
                CleanupStagingOutput(stagingOutput);
            }
        }

        var resultMessage = failures == 0
            ? $"Document conversion complete: {converted:N0} converted, {skipped:N0} already converted or skipped."
            : $"Document conversion completed with {failures:N0} failure(s): "
              + $"{converted:N0} converted, {skipped:N0} skipped. Successful outputs were preserved.";
        await LogAndReportAsync(context, resultMessage, CancellationToken.None);
        var result = new DocumentConversionResult(
            failures == 0,
            false,
            resultMessage,
            pendingDocuments.Count,
            converted,
            skipped,
            failures);
        if (result.Converted > 0 && context.RefreshDocumentsAsync is not null)
        {
            await context.RefreshDocumentsAsync(cancellationToken);
        }

        return result;

        async Task LogItemFailureAsync(string path, string detail)
        {
            var message = $"Document conversion failed for '{path}': {detail}.";
            await log.WriteAsync(message, CancellationToken.None);
            context.ReportStatus(message);
        }
    }

    internal static IReadOnlyList<PendingDocument> FindPendingDocuments(
        string vaultPath,
        bool recursive)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var convertedInputPaths = FindConvertedInputPaths(vaultPath, enumerationOptions);

        return Directory.EnumerateFiles(vaultPath, "*", enumerationOptions)
            .Where(IsSupportedDocument)
            .Select(path => new FileInfo(path))
            .GroupBy(
                file => Path.ChangeExtension(file.FullName, ".md"),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(file => LegacyExtensions.Contains(file.Extension) ? 1 : 0)
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .First())
            .Select(file => new PendingDocument(
                file.FullName,
                Path.ChangeExtension(file.FullName, ".md"),
                file.LastWriteTimeUtc))
            // A conversion source marker stays with its Markdown note through renames, preventing regeneration at the old basename.
            .Where(document => !File.Exists(document.OutputPath)
                               && !convertedInputPaths.Contains(document.InputPath))
            .OrderByDescending(document => document.LastWriteTimeUtc)
            .ThenBy(document => document.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<string> FindConvertedInputPaths(
        string vaultPath,
        EnumerationOptions enumerationOptions)
    {
        var normalizedVaultPath = Path.GetFullPath(vaultPath);
        var convertedInputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var markdownPath in Directory.EnumerateFiles(
                     normalizedVaultPath,
                     "*.md",
                     enumerationOptions))
        {
            try
            {
                var markdownFolder = Path.GetDirectoryName(markdownPath)!;
                foreach (var sourceReference in MarkdownMetadataParser
                             .ParseDocumentConversionSourceReferences(File.ReadAllText(markdownPath)))
                {
                    var sourcePath = Path.GetFullPath(Path.Combine(
                        markdownFolder,
                        ObsidianEmbedTarget.GetLiteralPath(sourceReference)
                            .Replace('/', Path.DirectorySeparatorChar)));
                    if (IsPathInsideVault(sourcePath, normalizedVaultPath))
                    {
                        convertedInputPaths.Add(sourcePath);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or UriFormatException)
            {
                // An unreadable or malformed Markdown note must not stop unrelated conversion work.
            }
        }

        return convertedInputPaths;
    }

    private static bool IsPathInsideVault(string path, string vaultPath)
    {
        var relativePath = Path.GetRelativePath(vaultPath, path);
        return !Path.IsPathRooted(relativePath)
               && !relativePath.Equals("..", StringComparison.Ordinal)
               && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsSupportedDocument(string path)
    {
        var extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension))
        {
            return false;
        }

        // Word uses tilde-prefixed .docx files for transient owner/lock metadata, not document content.
        return !extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
               || !Path.GetFileName(path).StartsWith("~", StringComparison.Ordinal);
    }

    private static string PrepareTextInput(
        PendingDocument document,
        string stagingDirectory)
    {
        if (!IsPlainTextDocument(document.InputPath))
        {
            return document.InputPath;
        }

        var stagingInputPath = Path.Combine(
            stagingDirectory,
            Path.GetFileName(document.InputPath));
        // MarkItDown treats no-BOM text as ASCII, so stage a UTF-8 BOM copy without modifying the source file.
        File.WriteAllText(
            stagingInputPath,
            ReadPlainText(document.InputPath),
            Utf8WithByteOrderMark);
        return stagingInputPath;
    }

    private static bool IsPlainTextDocument(string path) =>
        Path.GetExtension(path) is ".txt" or ".text";

    private static string ReadPlainText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (StartsWith(bytes, Utf32LittleEndianByteOrderMark))
        {
            return Utf32LittleEndian.GetString(bytes, Utf32LittleEndianByteOrderMark.Length,
                bytes.Length - Utf32LittleEndianByteOrderMark.Length);
        }

        if (StartsWith(bytes, Utf32BigEndianByteOrderMark))
        {
            return Utf32BigEndian.GetString(bytes, Utf32BigEndianByteOrderMark.Length,
                bytes.Length - Utf32BigEndianByteOrderMark.Length);
        }

        if (StartsWith(bytes, Utf8ByteOrderMark))
        {
            return StrictUtf8.GetString(bytes, Utf8ByteOrderMark.Length,
                bytes.Length - Utf8ByteOrderMark.Length);
        }

        if (StartsWith(bytes, Utf16LittleEndianByteOrderMark))
        {
            return Utf16LittleEndian.GetString(bytes, Utf16LittleEndianByteOrderMark.Length,
                bytes.Length - Utf16LittleEndianByteOrderMark.Length);
        }

        if (StartsWith(bytes, Utf16BigEndianByteOrderMark))
        {
            return Utf16BigEndian.GetString(bytes, Utf16BigEndianByteOrderMark.Length,
                bytes.Length - Utf16BigEndianByteOrderMark.Length);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            // Reject unknown encodings instead of silently replacing characters in the generated Markdown.
            throw new InvalidDataException(
                "The text file is not valid UTF-8, UTF-16, or UTF-32 text.",
                exception);
        }
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix) =>
        bytes.Length >= prefix.Length
        && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool IsPasswordProtectedPdf(
        PendingDocument document,
        CliItemResult item)
    {
        if (!Path.GetExtension(document.InputPath)
            .Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // DOC2MD emits this structured error for encrypted PDFs before it creates Markdown output.
        return item.Error?.Contains("encrypted", StringComparison.OrdinalIgnoreCase) == true
               && item.Error?.Contains("password", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static StagingOutput CreateStagingOutput(string outputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        // A unique child directory confines converter artifacts to the operation that created them.
        var stagingDirectory = Path.Combine(
            directory,
            $".notemanager-doc2md-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        return new StagingOutput(
            stagingDirectory,
            Path.Combine(stagingDirectory, Path.GetFileName(outputPath)));
    }

    private static bool TryPublishStagedDocument(
        string stagedOutputPath,
        string outputPath)
    {
        try
        {
            // Moving within the destination directory keeps publication atomic and rejects a concurrent destination.
            File.Move(stagedOutputPath, outputPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(outputPath))
        {
            return false;
        }
    }

    private static void CleanupStagingOutput(StagingOutput stagingOutput)
    {
        try
        {
            if (Directory.Exists(stagingOutput.DirectoryPath))
            {
                // The unique directory is operation-owned, so recursive cleanup cannot target another writer's files.
                Directory.Delete(stagingOutput.DirectoryPath, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The individual conversion failure remains the primary outcome.
        }
    }

    private static void AppendOriginalPdfEmbed(
        PendingDocument document,
        string stagedOutputPath)
    {
        if (!Path.GetExtension(document.InputPath)
            .Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var outputDirectory = Path.GetDirectoryName(document.OutputPath)!;
        var sourcePath = Path.GetRelativePath(outputDirectory, document.InputPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var escapedSourcePath = NoteManager.App.Services.ObsidianEmbedTarget.EscapeLiteralPath(sourcePath);

        // Keep the source PDF discoverable from generated Markdown as its durable conversion relationship.
        File.AppendAllText(
            stagedOutputPath,
            $"{Environment.NewLine}{Environment.NewLine}![[{escapedSourcePath}]]");
    }

    private static void AppendConversionSourceReference(
        PendingDocument document,
        string stagedOutputPath)
    {
        var outputDirectory = Path.GetDirectoryName(document.OutputPath)!;
        var sourcePath = Path.GetRelativePath(outputDirectory, document.InputPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        // URI escaping makes the metadata a stable literal path even when a filename contains comment or Markdown delimiters.
        var escapedSourcePath = Uri.EscapeDataString(sourcePath);

        // Persist this relationship inside the output because File.Move carries it through a note rename without touching shared sources.
        File.AppendAllText(
            stagedOutputPath,
            $"{Environment.NewLine}{Environment.NewLine}<!-- notemanager-conversion-source: {escapedSourcePath} -->");
    }

    private static bool TryReadItemResult(
        string standardOutput,
        out CliItemResult result)
    {
        result = new CliItemResult(false, false, null);
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            // DOC2MD is a protocol boundary: only an object with both boolean outcome flags may control publication.
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadRequiredBoolean(root, "succeeded", out var succeeded)
                || !TryReadRequiredBoolean(root, "skipped", out var skipped))
            {
                return false;
            }

            result = new CliItemResult(
                succeeded,
                skipped,
                root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadRequiredBoolean(
        JsonElement item,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!item.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        return property.ValueKind == JsonValueKind.False;
    }

    private async Task LogAndReportAsync(
        PluginHostContext context,
        string message,
        CancellationToken cancellationToken)
    {
        await log.WriteAsync(message, cancellationToken);
        context.ReportStatus(message);
    }

    private static string Bound(string value)
    {
        var normalized = value.Trim();
        return normalized.Length switch
        {
            0 => "no diagnostic output",
            > 1000 => normalized[..1000] + "…",
            _ => normalized
        };
    }

    internal sealed record PendingDocument(
        string InputPath,
        string OutputPath,
        DateTime LastWriteTimeUtc);

    // This record explicitly marks the directory and file that a single conversion operation owns.
    private sealed record StagingOutput(string DirectoryPath, string OutputPath);

    // Preserve DOC2MD's structured error so expected document states can be reported separately from failures.
    private sealed record CliItemResult(bool Succeeded, bool Skipped, string? Error);
}
