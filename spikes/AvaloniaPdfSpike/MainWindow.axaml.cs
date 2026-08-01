using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaloniaPdfSpike;

public partial class MainWindow : Window
{
    private readonly Uri _pdfUri;
    private int _completedNavigations;

    public MainWindow()
    {
        InitializeComponent();

        var pdfPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "Assets", "orbital-guide.pdf"));
        _pdfUri = new Uri(pdfPath);
        PathText.Text = pdfPath;

        PdfWebView.AdapterCreated += (_, _) =>
            StatusText.Text = "Native WebView created; loading local PDF…";
        SecondPdfWebView.AdapterCreated += (_, _) =>
            StatusText.Text = "Second NativeWebView created; loading local PDF…";
        PdfWebView.NavigationCompleted += (_, _) => RecordNavigationCompleted();
        SecondPdfWebView.NavigationCompleted += (_, _) => RecordNavigationCompleted();

        Opened += (_, _) => LoadPdf();
    }

    private void ReloadButton_OnClick(object? sender, RoutedEventArgs e)
        => LoadPdf();

    private void PrintButton_OnClick(object? sender, RoutedEventArgs e)
        => PdfWebView.ShowPrintUI();

    private void LoadPdf()
    {
        if (!File.Exists(_pdfUri.LocalPath))
        {
            StatusText.Text = "FAIL: copied PDF was not found";
            return;
        }

        _completedNavigations = 0;
        StatusText.Text = "PDF found; requesting NativeWebView navigation…";
        PdfWebView.Source = _pdfUri;
        SecondPdfWebView.Source = _pdfUri;
    }

    private void RecordNavigationCompleted()
    {
        _completedNavigations++;
        StatusText.Text = _completedNavigations >= 2
            ? "PASS: two local PDFs completed in separate NativeWebViews"
            : "First local PDF completed; waiting for second viewer…";
    }
}
