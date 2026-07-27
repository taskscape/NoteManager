using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace NoteManager.App.Controls;

public partial class InteractivePdfViewer : UserControl
{
    public static readonly DependencyProperty PdfPathProperty = DependencyProperty.Register(
        nameof(PdfPath),
        typeof(string),
        typeof(InteractivePdfViewer),
        new PropertyMetadata(string.Empty, OnPdfPathChanged));

    private int _navigationVersion;
    private bool _navigationStarted;
    private ScrollViewer? _hostScrollViewer;

    public InteractivePdfViewer()
    {
        InitializeComponent();
        Loaded += InteractivePdfViewer_OnLoaded;
        Unloaded += InteractivePdfViewer_OnUnloaded;
        SizeChanged += (_, _) => TryStartNavigation();
    }

    public string PdfPath
    {
        get => (string)GetValue(PdfPathProperty);
        set => SetValue(PdfPathProperty, value);
    }

    private static void OnPdfPathChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is InteractivePdfViewer viewer)
        {
            viewer.PreparePath(eventArgs.NewValue as string ?? string.Empty);
        }
    }

    private void PreparePath(string pdfPath)
    {
        _navigationVersion++;
        _navigationStarted = false;
        FileNameTextBlock.Text = Path.GetFileName(pdfPath);
        ShowStatus("Scroll here to load the interactive PDF viewer.", isLoading: false);
        TryStartNavigation();
    }

    private void InteractivePdfViewer_OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostScrollViewer = FindVisualAncestor<ScrollViewer>(this);
        if (_hostScrollViewer is not null)
        {
            _hostScrollViewer.ScrollChanged += HostScrollViewer_OnScrollChanged;
        }

        TryStartNavigation();
    }

    private void InteractivePdfViewer_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostScrollViewer is not null)
        {
            _hostScrollViewer.ScrollChanged -= HostScrollViewer_OnScrollChanged;
            _hostScrollViewer = null;
        }
    }

    private void HostScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
        => TryStartNavigation();

    private void TryStartNavigation()
    {
        if (_navigationStarted
            || !IsLoaded
            || string.IsNullOrWhiteSpace(PdfPath)
            || !IsInViewport())
        {
            return;
        }

        _navigationStarted = true;
        var navigationVersion = _navigationVersion;
        _ = NavigateAsync(PdfPath, navigationVersion);
    }

    private bool IsInViewport()
    {
        if (_hostScrollViewer is null)
        {
            return true;
        }

        try
        {
            var origin = TransformToAncestor(_hostScrollViewer).Transform(new Point());
            var bounds = new Rect(origin, RenderSize);
            var viewport = new Rect(
                0,
                -160,
                _hostScrollViewer.ViewportWidth,
                _hostScrollViewer.ViewportHeight + 320);
            return bounds.IntersectsWith(viewport);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task NavigateAsync(string pdfPath, int navigationVersion)
    {
        ShowStatus("Loading interactive PDF viewer…", isLoading: true);

        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            ShowStatus($"Embedded PDF was not found:{Environment.NewLine}{pdfPath}", isLoading: false);
            return;
        }

        try
        {
            await PdfWebView.EnsureCoreWebView2Async();
            if (navigationVersion != _navigationVersion)
            {
                return;
            }

            PdfWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            PdfWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            PdfWebView.CoreWebView2.Settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.None;
            PdfWebView.NavigationCompleted -= PdfWebView_OnNavigationCompleted;
            PdfWebView.NavigationCompleted += PdfWebView_OnNavigationCompleted;
            PdfWebView.CoreWebView2.Navigate(new Uri(pdfPath).AbsoluteUri);
        }
        catch (Exception exception)
        {
            ShowStatus($"The interactive PDF viewer could not be started.{Environment.NewLine}{exception.Message}", isLoading: false);
        }
    }

    private void PdfWebView_OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            StatusOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        ShowStatus($"The PDF could not be displayed ({eventArgs.WebErrorStatus}).", isLoading: false);
    }

    private void ShowStatus(string message, bool isLoading)
    {
        StatusTextBlock.Text = message;
        LoadingProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private void OpenExternallyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(PdfPath))
        {
            ShowStatus($"Embedded PDF was not found:{Environment.NewLine}{PdfPath}", isLoading: false);
            return;
        }

        Process.Start(new ProcessStartInfo(PdfPath) { UseShellExecute = true });
    }

    private static T? FindVisualAncestor<T>(DependencyObject child)
        where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T ancestor)
            {
                return ancestor;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
