using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.VisualTree;
using NoteManager.App.Services;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NoteManager.Desktop.Controls;

public partial class PdfViewer : UserControl
{
    private const string NoteScrollMessageType = "note-scroll";

    private const string NoteScrollBridgeScript = """
        (() => {
            if (window.__noteManagerNoteScrollBridgeInstalled) {
                return;
            }

            window.__noteManagerNoteScrollBridgeInstalled = true;
            window.addEventListener('wheel', event => {
                if (event.defaultPrevented
                    || event.deltaY === 0
                    || event.ctrlKey
                    || event.metaKey
                    || event.altKey
                    || event.shiftKey) {
                    return;
                }

                event.preventDefault();
                invokeCSharpAction(JSON.stringify({
                    type: 'note-scroll',
                    deltaY: event.deltaY,
                    deltaMode: event.deltaMode
                }));
            }, { capture: true, passive: false });
        })();
        """;

    public static readonly StyledProperty<string?> PdfPathProperty =
        AvaloniaProperty.Register<PdfViewer, string?>(nameof(PdfPath));

    public PdfViewer()
    {
        InitializeComponent();
        WebView.EnvironmentRequested += WebView_OnEnvironmentRequested;
        WebView.NavigationCompleted += WebView_OnNavigationCompleted;
        WebView.WebMessageReceived += WebView_OnWebMessageReceived;
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
        try
        {
            WebView.Source = new Uri(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or NotSupportedException
            or COMException
            or UnauthorizedAccessException
            or ArgumentException
            or UriFormatException)
        {
            new ApplicationActivityLog().TryWriteUnhandledException(
                "PdfViewer.Navigate",
                exception);
            StatusText.Text = $"The PDF could not be displayed: {exception.Message}";
        }
    }

    private void WebView_OnEnvironmentRequested(
        object? sender,
        WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is not WindowsWebView2EnvironmentRequestedEventArgs webView2)
        {
            return;
        }

        var userDataFolder = ApplicationDataPaths.WebView2UserDataFolder;
        try
        {
            Directory.CreateDirectory(userDataFolder);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            new ApplicationActivityLog().TryWriteUnhandledException(
                "PdfViewer.WebViewEnvironment",
                exception);
            StatusText.Text = $"The PDF could not be displayed: {exception.Message}";
        }

        webView2.UserDataFolder = userDataFolder;
    }

    private async void WebView_OnNavigationCompleted(
        object? sender,
        WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            StatusText.Text = "The PDF could not be displayed";
            return;
        }

        StatusText.Text = "Interactive PDF ready";
        try
        {
            await WebView.InvokeScript(NoteScrollBridgeScript);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or NotSupportedException
            or COMException)
        {
            // The PDF remains usable when a platform WebView does not support
            // script injection. The always-visible note scrollbar is the fallback.
        }
    }

    private void WebView_OnWebMessageReceived(
        object? sender,
        WebMessageReceivedEventArgs e)
    {
        if (!TryReadNoteScrollMessage(e.Body, out var deltaY, out var deltaMode))
        {
            return;
        }

        var noteScrollViewer = this
            .GetVisualAncestors()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (noteScrollViewer is not null)
        {
            NoteScrollCoordinator.ScrollBy(noteScrollViewer, deltaY, deltaMode);
        }
    }

    private static bool TryReadNoteScrollMessage(
        string? body,
        out double deltaY,
        out ScrollDeltaMode deltaMode)
    {
        deltaY = 0;
        deltaMode = ScrollDeltaMode.Pixel;

        try
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || type.GetString() != NoteScrollMessageType
                || !root.TryGetProperty("deltaY", out var delta)
                || !delta.TryGetDouble(out deltaY)
                || !double.IsFinite(deltaY)
                || !root.TryGetProperty("deltaMode", out var mode)
                || !mode.TryGetInt32(out var modeValue)
                || !Enum.IsDefined((ScrollDeltaMode)modeValue))
            {
                return false;
            }

            deltaMode = (ScrollDeltaMode)modeValue;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
