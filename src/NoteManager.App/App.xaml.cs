using System.Windows;
using NoteManager.App.Infrastructure;
using NoteManager.App.Services;

namespace NoteManager.App;

public partial class App : Application
{
    private UiAutomationServer? _automationServer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = ApplicationOptions.Parse(e.Args);
        var window = new MainWindow(
            options.FolderPath,
            options.InfostackerBaseUri);
        MainWindow = window;
        window.Show();

        if (options.AutomationPipeName is null)
        {
            return;
        }

        _automationServer = new UiAutomationServer(
            options.AutomationPipeName,
            async (folderPath, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await window.Dispatcher
                    .InvokeAsync(() => window.ChangeFolderAsync(folderPath))
                    .Task
                    .Unwrap();
            },
            async (pdfPath, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await window.Dispatcher
                    .InvokeAsync(
                        () => window.ImportPdfForAutomationAsync(pdfPath))
                    .Task
                    .Unwrap();
            },
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await window.Dispatcher
                    .InvokeAsync(window.OpenSharePanelForAutomation)
                    .Task;
            });
        _automationServer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _automationServer?.Dispose();
        _automationServer = null;
        base.OnExit(e);
    }
}
