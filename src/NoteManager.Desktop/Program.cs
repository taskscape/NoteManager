using Avalonia;
using Avalonia.Threading;
using NoteManager.App.Services;

namespace NoteManager.Desktop;

internal static class Program
{
    private static readonly ApplicationActivityLog ActivityLog = new();

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            ActivityLog.TryWriteUnhandledException("Program.Main", exception);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
#if DEBUG
            .WithDeveloperTools()
#endif
            .AfterSetup(_ =>
            {
                Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
            })
            .LogToTrace();

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => ActivityLog.TryWriteUnhandledException(
            e.IsTerminating
                ? "AppDomain.UnhandledException (terminating)"
                : "AppDomain.UnhandledException",
            e.ExceptionObject);

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        ActivityLog.TryWriteUnhandledException(
            "TaskScheduler.UnobservedTaskException",
            e.Exception);
        e.SetObserved();
    }

    private static void OnDispatcherUnhandledException(
        object? sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        ActivityLog.TryWriteUnhandledException("UI dispatcher", e.Exception);
        e.Handled = UnhandledUiExceptionPolicy.ShouldKeepApplicationRunning(e.Exception);
    }
}
