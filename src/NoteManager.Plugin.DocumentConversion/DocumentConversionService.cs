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
    DocumentConversionLog log)
{
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
        context.ReportStatus("Checking for documents that need Markdown counterparts…");
        var process = await runner.ConvertFolderAsync(
            context.VaultPath,
            cancellationToken);

        if (process.WasCancelled)
        {
            const string message = "Document conversion was cancelled.";
            await LogAndReportAsync(context, message, CancellationToken.None);
            return new DocumentConversionResult(false, true, message);
        }

        if (process.TimedOut)
        {
            var message =
                $"Document conversion exceeded its timeout after {process.Duration.TotalMinutes:N0} minute(s).";
            await LogAndReportAsync(context, message, CancellationToken.None);
            return new DocumentConversionResult(false, false, message, Failures: 1);
        }

        if (!TryReadSummary(process.StandardOutput, out var summary))
        {
            var detail = Bound(process.StandardError);
            var message = process.Succeeded
                ? "DOC2MD.Cli returned an unreadable conversion summary."
                : $"DOC2MD.Cli failed with exit code {process.ExitCode}: {detail}";
            await LogAndReportAsync(context, message, CancellationToken.None);
            return new DocumentConversionResult(false, false, message, Failures: 1);
        }

        var resultMessage = summary.Failures == 0
            ? $"Document conversion complete: {summary.Converted:N0} converted, "
              + $"{summary.ExistingOrSkipped:N0} already converted or skipped."
            : $"Document conversion completed with {summary.Failures:N0} failure(s): "
              + $"{summary.Converted:N0} converted, {summary.ExistingOrSkipped:N0} skipped.";
        if (!string.IsNullOrWhiteSpace(process.StandardError))
        {
            resultMessage += $" DOC2MD: {Bound(process.StandardError)}";
        }

        await LogAndReportAsync(context, resultMessage, CancellationToken.None);
        return summary with
        {
            Succeeded = process.Succeeded && summary.Failures == 0,
            Message = resultMessage
        };
    }

    private static bool TryReadSummary(
        string standardOutput,
        out DocumentConversionResult summary)
    {
        summary = new DocumentConversionResult(
            false,
            false,
            "DOC2MD.Cli did not return a summary.");
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            var total = root.GetProperty("total").GetInt32();
            var failures = root.GetProperty("failures").GetInt32();
            var converted = 0;
            var skipped = 0;
            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("succeeded", out var succeeded)
                    && succeeded.ValueKind == JsonValueKind.True)
                {
                    converted++;
                }
                else if (item.TryGetProperty("skipped", out var wasSkipped)
                         && wasSkipped.ValueKind == JsonValueKind.True)
                {
                    skipped++;
                }
            }

            summary = new DocumentConversionResult(
                failures == 0,
                false,
                string.Empty,
                total,
                converted,
                skipped,
                failures);
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException
            or InvalidOperationException
            or KeyNotFoundException)
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
}
