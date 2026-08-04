using NoteManager.Plugin.DocumentConversion;
using NoteManager.Plugins;
using Xunit;

namespace NoteManager.Plugin.DocumentConversion.Tests;

[Trait("Category", "Integration")]
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
        var cliPath = Path.Combine(folder.Path, "DOC2MD.Cli.exe");
        File.WriteAllText(cliPath, string.Empty);
        await using var plugin = new DocumentConversionPlugin(cliPath);

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

    [Fact]
    public async Task StartAsync_FailsWithInstalledPathGuidanceWhenDoc2MdIsMissing()
    {
        using var folder = new TemporaryFolder();
        var missingCliPath = Path.Combine(folder.Path, "missing", "DOC2MD.Cli.exe");
        var statuses = new List<string>();
        var context = new PluginHostContext(
            folder.Path,
            Path.Combine(folder.Path, ".note", "plugins", "document-conversion"),
            _ => Task.FromResult(true),
            statuses.Add);
        await using var plugin = new DocumentConversionPlugin(missingCliPath);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => plugin.StartAsync(context));

        Assert.Contains(missingCliPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            statuses,
            status => status.Contains("Install DOC2MD", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultCliExecutablePath_UsesTheDOC2MDInstallerLocation()
    {
        Assert.Equal(
            @"C:\Program Files\Taskscape\DOC2MD\DOC2MD.Cli.exe",
            DocumentConversionPlugin.DefaultCliExecutablePath);
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
