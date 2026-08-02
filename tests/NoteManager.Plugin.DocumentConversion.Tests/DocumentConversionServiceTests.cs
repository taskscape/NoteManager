using NoteManager.Plugin.DocumentConversion;
using NoteManager.Plugins;
using Xunit;

namespace NoteManager.Plugin.DocumentConversion.Tests;

public sealed class DocumentConversionServiceTests
{
    [Fact]
    public async Task ConvertPendingAsync_ReportsConvertedSkippedAndFailedItems()
    {
        using var folder = new TemporaryFolder();
        var statuses = new List<string>();
        var context = new PluginHostContext(
            folder.Path,
            Path.Combine(folder.Path, ".note", "plugins", "document-conversion"),
            _ => Task.FromResult(true),
            statuses.Add);
        var runner = new StubRunner(new Doc2MdProcessResult(
            1,
            """
            {
              "succeeded": false,
              "total": 3,
              "failures": 1,
              "items": [
                { "succeeded": true },
                { "succeeded": false, "skipped": true },
                { "succeeded": false, "error": "broken" }
              ]
            }
            """,
            string.Empty,
            TimeSpan.FromSeconds(1),
            false,
            false));
        var service = new DocumentConversionService(
            runner,
            new DocumentConversionLog(context.ConfigurationDirectory));

        var result = await service.ConvertPendingAsync(context);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Total);
        Assert.Equal(1, result.Converted);
        Assert.Equal(1, result.ExistingOrSkipped);
        Assert.Equal(1, result.Failures);
        Assert.Contains(statuses, status => status.Contains(
            "completed with 1 failure",
            StringComparison.Ordinal));
    }

    private sealed class StubRunner(Doc2MdProcessResult result)
        : IDoc2MdProcessRunner
    {
        public Task<Doc2MdProcessResult> ConvertFolderAsync(
            string vaultPath,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
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
