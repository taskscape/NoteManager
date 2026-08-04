using System.Text.Json;
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
            var existingTemporaryOutputs = FindAtomicTemporaryOutputs(
                document.OutputPath);
            var process = await runner.ConvertFileAsync(
                document.InputPath,
                document.OutputPath,
                cancellationToken);

            if (process.WasCancelled)
            {
                CleanupFailedDocument(document.OutputPath, existingTemporaryOutputs);
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
                CleanupFailedDocument(document.OutputPath, existingTemporaryOutputs);
                await LogItemFailureAsync(
                    relativePath,
                    $"exceeded the {options.CommandTimeoutMinutes:N0}-minute timeout");
                continue;
            }

            if (!TryReadItemResult(process.StandardOutput, out var item))
            {
                failures++;
                CleanupFailedDocument(document.OutputPath, existingTemporaryOutputs);
                var detail = process.Succeeded
                    ? "returned an unreadable JSON result"
                    : $"failed with exit code {process.ExitCode}: {Bound(process.StandardError)}";
                await LogItemFailureAsync(relativePath, detail);
                continue;
            }

            if (item.Skipped)
            {
                skipped++;
                continue;
            }

            if (process.Succeeded
                && item.Succeeded
                && File.Exists(document.OutputPath))
            {
                converted++;
                continue;
            }

            failures++;
            CleanupFailedDocument(document.OutputPath, existingTemporaryOutputs);
            var failureDetail = string.IsNullOrWhiteSpace(process.StandardError)
                ? "DOC2MD reported that the document was not converted"
                : Bound(process.StandardError);
            await LogItemFailureAsync(relativePath, failureDetail);
        }

        var resultMessage = failures == 0
            ? $"Document conversion complete: {converted:N0} converted, {skipped:N0} already converted or skipped."
            : $"Document conversion completed with {failures:N0} failure(s): "
              + $"{converted:N0} converted, {skipped:N0} skipped. Successful outputs were preserved.";
        await LogAndReportAsync(context, resultMessage, CancellationToken.None);
        return new DocumentConversionResult(
            failures == 0,
            false,
            resultMessage,
            pendingDocuments.Count,
            converted,
            skipped,
            failures);

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

        return Directory.EnumerateFiles(vaultPath, "*", enumerationOptions)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
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
            .Where(document => !File.Exists(document.OutputPath))
            .OrderByDescending(document => document.LastWriteTimeUtc)
            .ThenBy(document => document.InputPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<string> FindAtomicTemporaryOutputs(string outputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, ".doc2md-*.tmp")
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void CleanupFailedDocument(
        string outputPath,
        IReadOnlySet<string> existingTemporaryOutputs)
    {
        TryDelete(outputPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var temporaryOutput in Directory.EnumerateFiles(
                     directory,
                     ".doc2md-*.tmp"))
        {
            if (!existingTemporaryOutputs.Contains(temporaryOutput))
            {
                TryDelete(temporaryOutput);
            }
        }
    }

    private static void TryDelete(string path)
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
            // The individual conversion failure remains the primary outcome.
        }
    }

    private static bool TryReadItemResult(
        string standardOutput,
        out CliItemResult result)
    {
        result = new CliItemResult(false, false);
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            result = new CliItemResult(
                root.TryGetProperty("succeeded", out var succeeded)
                && succeeded.ValueKind == JsonValueKind.True,
                root.TryGetProperty("skipped", out var skipped)
                && skipped.ValueKind == JsonValueKind.True);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private sealed record CliItemResult(bool Succeeded, bool Skipped);
}
