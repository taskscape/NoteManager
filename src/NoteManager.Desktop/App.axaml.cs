using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NoteManager.App.Infrastructure;

namespace NoteManager.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = ApplicationOptions.Parse(desktop.Args ?? []);
            desktop.MainWindow = new MainWindow(options);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
