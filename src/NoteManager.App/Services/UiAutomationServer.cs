using System.IO;
using System.IO.Pipes;
using System.Text;

namespace NoteManager.App.Services;

internal sealed class UiAutomationServer : IDisposable
{
    private const string FolderCommandPrefix = "folder|";
    private const string ImportPdfCommandPrefix = "import-pdf|";
    private const string OpenShareCommand = "open-share";

    private readonly string _pipeName;
    private readonly Func<string, CancellationToken, Task> _changeFolder;
    private readonly Func<string, CancellationToken, Task>? _importPdf;
    private readonly Func<CancellationToken, Task>? _openShare;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listenerTask;

    public UiAutomationServer(
        string pipeName,
        Func<string, CancellationToken, Task> changeFolder,
        Func<string, CancellationToken, Task>? importPdf = null,
        Func<CancellationToken, Task>? openShare = null)
    {
        _pipeName = pipeName;
        _changeFolder = changeFolder;
        _importPdf = importPdf;
        _openShare = openShare;
    }

    public void Start()
    {
        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("The folder automation server is already running.");
        }

        _listenerTask = Task.Run(() => ListenAsync(_cancellation.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ListenForOneCommandAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private async Task ListenForOneCommandAsync(CancellationToken cancellationToken)
    {
        await using var server = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync(cancellationToken);

        using var reader = new StreamReader(server);
        var command = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        string response;
        try
        {
            await ExecuteCommandAsync(command, cancellationToken);
            response = "OK";
        }
        catch (Exception exception)
        {
            response =
                $"ERROR|{exception.GetType().Name}|{exception.Message}";
        }

        var responseBytes = Encoding.UTF8.GetBytes(response + "\n");
        await server.WriteAsync(responseBytes, cancellationToken);
        await server.FlushAsync(cancellationToken);
    }

    private async Task ExecuteCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        if (command.StartsWith(
                ImportPdfCommandPrefix,
                StringComparison.Ordinal))
        {
            if (_importPdf is null)
            {
                return;
            }

            var pdfPath = command[ImportPdfCommandPrefix.Length..];
            await _importPdf(Path.GetFullPath(pdfPath), cancellationToken);
            return;
        }

        if (command.Equals(OpenShareCommand, StringComparison.Ordinal))
        {
            if (_openShare is not null)
            {
                await _openShare(cancellationToken);
            }

            return;
        }

        var folderPath = command.StartsWith(
                FolderCommandPrefix,
                StringComparison.Ordinal)
            ? command[FolderCommandPrefix.Length..]
            : command;
        await _changeFolder(Path.GetFullPath(folderPath), cancellationToken);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
