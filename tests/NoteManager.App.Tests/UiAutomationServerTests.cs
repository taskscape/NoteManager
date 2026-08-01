using System.IO.Pipes;
using NoteManager.App.Services;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class UiAutomationServerTests
{
    [Fact]
    public async Task FolderCommand_RunsAcrossCurrentPlatformNamedPipe()
    {
        var pipeName = $"nm-{Guid.NewGuid():N}"[..15];
        var receivedFolder = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new UiAutomationServer(
            pipeName,
            (folder, _) =>
            {
                receivedFolder.TrySetResult(folder);
                return Task.CompletedTask;
            });
        server.Start();

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(timeout.Token);

        await using var writer = new StreamWriter(client, leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(client, leaveOpen: true);
        var expectedFolder = Path.GetFullPath(Path.GetTempPath());

        await writer.WriteLineAsync($"folder|{expectedFolder}");
        var response = await reader.ReadLineAsync(timeout.Token);

        Assert.Equal("OK", response);
        Assert.Equal(
            expectedFolder,
            await receivedFolder.Task.WaitAsync(timeout.Token));
    }
}
