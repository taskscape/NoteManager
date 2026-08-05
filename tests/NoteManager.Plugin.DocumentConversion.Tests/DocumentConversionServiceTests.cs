using NoteManager.Plugins;
using Xunit;

namespace NoteManager.Plugin.DocumentConversion.Tests;

[Trait("Category", "Unit")]
public sealed class DocumentConversionServiceTests
{
    [Fact]
    public async Task ConvertPendingAsync_PreservesSuccessfulOutputAndCleansOnlyFailedDocument()
    {
        using var folder = new TemporaryFolder();
        var olderInput = Path.Combine(folder.Path, "older.txt");
        var newerInput = Path.Combine(folder.Path, "newer.txt");
        File.WriteAllText(olderInput, "older");
        File.WriteAllText(newerInput, "newer");
        File.SetLastWriteTimeUtc(olderInput, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(newerInput, DateTime.UtcNow);
        var existingTemporaryOutput = Path.Combine(
            folder.Path,
            $".doc2md-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(existingTemporaryOutput, "unrelated");
        var attemptedInputs = new List<string>();
        var runner = new StubRunner((input, output) =>
        {
            attemptedInputs.Add(input);
            if (input.Equals(newerInput, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(output, "converted");
                return SuccessResult();
            }

            File.WriteAllText(output, "partial");
            File.WriteAllText(
                Path.Combine(folder.Path, $".doc2md-{Guid.NewGuid():N}.tmp"),
                "partial");
            return FailureResult();
        });
        var context = CreateContext(folder.Path);

        var result = await new DocumentConversionService(
            runner,
            new DocumentConversionLog(context.ConfigurationDirectory),
            new DocumentConversionOptions()).ConvertPendingAsync(context);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Converted);
        Assert.Equal(1, result.Failures);
        Assert.Equal(newerInput, attemptedInputs[0]);
        Assert.Equal("converted", File.ReadAllText(Path.ChangeExtension(newerInput, ".md")));
        Assert.False(File.Exists(Path.ChangeExtension(olderInput, ".md")));
        Assert.True(File.Exists(existingTemporaryOutput));
        Assert.Single(Directory.EnumerateFiles(folder.Path, ".doc2md-*.tmp"));
    }

    [Fact]
    public void FindPendingDocuments_OrdersNewestFirstAndPrefersModernSource()
    {
        using var folder = new TemporaryFolder();
        var oldText = Path.Combine(folder.Path, "old.txt");
        var sharedLegacy = Path.Combine(folder.Path, "shared.doc");
        var sharedModern = Path.Combine(folder.Path, "shared.docx");
        File.WriteAllText(oldText, "old");
        File.WriteAllText(sharedLegacy, "legacy");
        File.WriteAllText(sharedModern, "modern");
        File.SetLastWriteTimeUtc(oldText, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(sharedLegacy, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(sharedModern, DateTime.UtcNow.AddMinutes(-1));

        var pending = DocumentConversionService.FindPendingDocuments(
            folder.Path,
            recursive: true);

        Assert.Equal(2, pending.Count);
        Assert.Equal(sharedModern, pending[0].InputPath);
        Assert.Equal(oldText, pending[1].InputPath);
    }

    [Fact]
    public async Task ConvertPendingAsync_ReportsConvertedSkippedAndFailedItems()
    {
        using var folder = new TemporaryFolder();
        var inputs = new[] { "success.txt", "skipped.txt", "failed.txt" };
        foreach (var input in inputs)
        {
            File.WriteAllText(Path.Combine(folder.Path, input), input);
        }

        var statuses = new List<string>();
        var context = CreateContext(folder.Path, statuses.Add);
        var runner = new StubRunner((input, output) =>
        {
            if (input.EndsWith("success.txt", StringComparison.Ordinal))
            {
                File.WriteAllText(output, "converted");
                return SuccessResult();
            }

            return input.EndsWith("skipped.txt", StringComparison.Ordinal)
                ? SkippedResult()
                : FailureResult();
        });

        var result = await new DocumentConversionService(
            runner,
            new DocumentConversionLog(context.ConfigurationDirectory),
            new DocumentConversionOptions()).ConvertPendingAsync(context);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Total);
        Assert.Equal(1, result.Converted);
        Assert.Equal(1, result.ExistingOrSkipped);
        Assert.Equal(1, result.Failures);
        Assert.Contains(statuses, status => status.Contains(
            "Successful outputs were preserved",
            StringComparison.Ordinal));
    }

    private static PluginHostContext CreateContext(
        string vaultPath,
        Action<string>? reportStatus = null) =>
        new(
            vaultPath,
            Path.Combine(vaultPath, ".note", "plugins", "document-conversion"),
            _ => Task.FromResult(true),
            reportStatus ?? (_ => { }));

    private static Doc2MdProcessResult SuccessResult() =>
        new(
            0,
            """{ "succeeded": true, "exitCode": 0 }""",
            string.Empty,
            TimeSpan.FromSeconds(1),
            false,
            false);

    private static Doc2MdProcessResult SkippedResult() =>
        new(
            0,
            """{ "succeeded": false, "skipped": true, "exitCode": 0 }""",
            string.Empty,
            TimeSpan.FromSeconds(1),
            false,
            false);

    private static Doc2MdProcessResult FailureResult() =>
        new(
            1,
            """{ "succeeded": false, "exitCode": 1 }""",
            "conversion failed",
            TimeSpan.FromSeconds(1),
            false,
            false);

    private sealed class StubRunner(
        Func<string, string, Doc2MdProcessResult> convert)
        : IDoc2MdProcessRunner
    {
        public Task<Doc2MdProcessResult> ConvertFileAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(convert(inputPath, outputPath));
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"NoteManager.DocumentConversion.{Guid.NewGuid():N}"))
                .FullName;
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
