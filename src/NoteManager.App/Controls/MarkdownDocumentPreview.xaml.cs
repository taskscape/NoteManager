using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using NoteManager.App.Models;

namespace NoteManager.App.Controls;

public partial class MarkdownDocumentPreview : UserControl
{
    private NoteItem? _note;
    private NoteItem? _subscribedNote;

    public MarkdownDocumentPreview()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachFromNote();
        _note = e.NewValue as NoteItem;
        AttachToNote();
        DocumentScrollViewer.ScrollToHome();
        RefreshPdfViewers();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachToNote();
        RefreshPdfViewers();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => DetachFromNote();

    private void AttachToNote()
    {
        if (!IsLoaded || _note is null || ReferenceEquals(_subscribedNote, _note))
        {
            return;
        }

        _note.PropertyChanged += OnNotePropertyChanged;
        _subscribedNote = _note;
    }

    private void DetachFromNote()
    {
        if (_subscribedNote is null)
        {
            return;
        }

        _subscribedNote.PropertyChanged -= OnNotePropertyChanged;
        _subscribedNote = null;
    }

    private void OnNotePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteItem.PdfReferences))
        {
            RefreshPdfViewers();
        }
    }

    private void RefreshPdfViewers()
    {
        PdfViewersItemsControl.ItemsSource = null;
        PdfViewersItemsControl.Visibility = Visibility.Collapsed;
        PdfSectionHeading.Visibility = Visibility.Collapsed;
        PdfStatusTextBlock.Visibility = Visibility.Collapsed;

        if (_note is not { IsMarkdownFile: true } note
            || note.PdfReferences.Length == 0)
        {
            return;
        }

        PdfSectionHeading.Text = note.PdfReferences.Length == 1
            ? "EMBEDDED PDF"
            : $"EMBEDDED PDFS ({note.PdfReferences.Length:N0})";
        PdfSectionHeading.Visibility = Visibility.Visible;
        PdfViewersItemsControl.ItemsSource = note.PdfReferences;
        PdfViewersItemsControl.Visibility = Visibility.Visible;
    }
}
