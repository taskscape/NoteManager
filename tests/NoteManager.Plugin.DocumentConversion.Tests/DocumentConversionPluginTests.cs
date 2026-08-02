using NoteManager.Plugin.DocumentConversion;
using NoteManager.Plugins;
using Xunit;

namespace NoteManager.Plugin.DocumentConversion.Tests;

public sealed class DocumentConversionPluginTests
{
    [Fact]
    public async Task StartAsync_CreatesConfigurationAndRunsTheFirstScanImmediately()
    {
        using var folder = new TemporaryFolder();
        var configurationDirectory = Path.Combine(
            folder.Path,
            ".note",
            "plugins",
            "document-conversion");
        var firstScan = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new PluginHostContext(
            folder.Path,
            configurationDirectory,
            _ => Task.FromResult(true),
            status =>
            {
                if (status.Contains(
                        "Document conversion complete",
                        StringComparison.Ordinal))
                {
                    firstScan.TrySetResult();
                }
            });
        await using var plugin = new DocumentConversionPlugin();

        await plugin.StartAsync(context);
        await firstScan.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(File.Exists(Path.Combine(
            configurationDirectory,
            "settings.json")));
        Assert.True(Directory.EnumerateFiles(
                Path.Combine(configurationDirectory, "logs"),
                "DocumentConversion-*.log")
            .Any());
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
