using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NoteManager.Desktop.Controls;

public partial class PdfViewer : UserControl
{
    public static readonly StyledProperty<string?> PdfPathProperty =
        AvaloniaProperty.Register<PdfViewer, string?>(nameof(PdfPath));

    public PdfViewer()
    {
        InitializeComponent();
        WebView.NavigationCompleted += (_, _) =>
            StatusText.Text = "Interactive PDF ready";
    }

    public string? PdfPath
    {
        get => GetValue(PdfPathProperty);
        set => SetValue(PdfPathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PdfPathProperty)
        {
            Navigate(change.NewValue as string);
        }
    }

    private void Navigate(string? path)
    {
        FileNameText.Text = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText.Text = $"PDF not found: {path}";
            return;
        }

        StatusText.Text = "Loading interactive PDF…";
        WebView.Source = new Uri(Path.GetFullPath(path));
    }

    private async void OpenExternally_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PdfPath) || !File.Exists(PdfPath))
        {
            StatusText.Text = $"PDF not found: {PdfPath}";
            return;
        }

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null || !await launcher.LaunchUriAsync(new Uri(Path.GetFullPath(PdfPath))))
        {
            StatusText.Text = "The operating system could not open this PDF";
        }
    }
}
