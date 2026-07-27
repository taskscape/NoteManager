using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using NoteManager.App.Controls;
using NoteManager.App.Models;
using NoteManager.App.Services;
using NoteManager.App.ViewModels;
using NoteManager.App.Views;

namespace NoteManager.App;

public partial class MainWindow : Window
{
    private const string DefaultNotesFolder = @"C:\Projects\Obsidian";
    private readonly string _startupNotesFolder;
    private bool _defaultFolderLoaded;

    public MainWindow()
        : this(null, null)
    {
    }

    public MainWindow(string? startupNotesFolder)
        : this(startupNotesFolder, null)
    {
    }

    public MainWindow(
        string? startupNotesFolder,
        Uri? infostackerBaseUri)
    {
        _startupNotesFolder = string.IsNullOrWhiteSpace(startupNotesFolder)
            ? DefaultNotesFolder
            : Path.GetFullPath(startupNotesFolder);
        InitializeComponent();
        DataContext = new MainViewModel(
            new InfostackerPublishingService(baseUri: infostackerBaseUri));
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        Closed += (_, _) => ViewModel.Dispose();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_defaultFolderLoaded)
        {
            return;
        }

        _defaultFolderLoaded = true;
        if (Directory.Exists(_startupNotesFolder))
        {
            await ChangeFolderAsync(_startupNotesFolder);
        }
        else
        {
            ViewModel.StatusText = $"Notes folder was not found: {_startupNotesFolder}";
        }
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    private void FindMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async void OpenFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
        => await OpenFolderAsync();

    private async Task OpenFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Open Markdown notes folder",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await ChangeFolderAsync(dialog.FolderName);
        }
    }

    public Task ChangeFolderAsync(string folderPath)
        => ViewModel.LoadMarkdownFolderAsync(folderPath);

    public Task ImportPdfForAutomationAsync(string pdfPath)
    {
        var selectedNote = ViewModel.SelectedNote;
        return selectedNote is null
            ? Task.CompletedTask
            : ViewModel.ImportPdfFilesAsync(
                selectedNote,
                new[] { pdfPath },
                insertionIndex: null);
    }

    public void OpenSharePanelForAutomation()
    {
        // A StaysOpen=false Popup requires foreground mouse capture. UI
        // automation runners may not own the foreground desktop, so keep the
        // real panel open explicitly for the opt-in automation session.
        SharePanelPopup.StaysOpen = true;
        ViewModel.IsSharePanelOpen = false;
        ViewModel.IsSharePanelOpen = true;
        SharePanelPopup.IsOpen = true;
    }

    private void NotePdf_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var pdfPaths = GetDroppedPdfPaths(e.Data);
        var target = ResolvePdfDropTarget(e.OriginalSource as DependencyObject);
        e.Effects = pdfPaths.Length > 0
                    && ViewModel.CanImportPdfIntoNote(target.Note)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void NotePdf_OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var pdfPaths = GetDroppedPdfPaths(e.Data);
        var target = ResolvePdfDropTarget(e.OriginalSource as DependencyObject);
        e.Handled = true;

        if (pdfPaths.Length == 0 || !ViewModel.CanImportPdfIntoNote(target.Note))
        {
            ViewModel.StatusText =
                "Drop PDF files onto a Markdown note or its editor";
            return;
        }

        await ViewModel.ImportPdfFilesAsync(
            target.Note!,
            pdfPaths,
            target.InsertionIndex);
    }

    private PdfDropTarget ResolvePdfDropTarget(DependencyObject? source)
    {
        var editor = FindVisualAncestor<TextBox>(source);
        if (editor?.DataContext is NoteItem editorNote
            && AutomationProperties.GetAutomationId(editor)
                .Equals("MarkdownEditor", StringComparison.Ordinal))
        {
            return new PdfDropTarget(editorNote, editor.CaretIndex);
        }

        var item = source is null
            ? null
            : ItemsControl.ContainerFromElement(NotesList, source)
                as ListBoxItem;
        if (item?.DataContext is NoteItem listNote)
        {
            return new PdfDropTarget(listNote, InsertionIndex: null);
        }

        var preview = FindVisualAncestor<MarkdownDocumentPreview>(source);
        return preview?.DataContext is NoteItem previewNote
            ? new PdfDropTarget(previewNote, InsertionIndex: null)
            : new PdfDropTarget(null, InsertionIndex: null);
    }

    private static string[] GetDroppedPdfPaths(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }

        return paths
            .Where(path => File.Exists(path)
                           && Path.GetExtension(path).Equals(
                               ".pdf",
                               StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
        => child is Visual or Visual3D
            ? VisualTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);

    private async void PublishShareLinkButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        var publicUrl = await ViewModel.PublishSelectedNoteAsync();
        if (publicUrl is null)
        {
            return;
        }

        try
        {
            await CopyToClipboardWithRetryAsync(publicUrl);
            ViewModel.ConfirmPublicLinkCopied(publicUrl);
        }
        catch (ExternalException exception)
        {
            ViewModel.ReportClipboardFailure(exception.Message);
        }
    }

    private static async Task CopyToClipboardWithRetryAsync(string text)
    {
        ExternalException? lastException = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (ExternalException exception)
            {
                lastException = exception;
                await Task.Delay(80);
            }
        }

        throw lastException
              ?? new ExternalException("The clipboard is unavailable.");
    }

    private async void DeleteNote_OnClick(object sender, RoutedEventArgs e)
    {
        var note = ViewModel.SelectedNote;
        if (note is null || !ViewModel.CanDeleteSelectedNote)
        {
            ViewModel.StatusText = "Select a Markdown note from the current folder before deleting";
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Permanently delete \"{note.FileName}\"?{Environment.NewLine}{Environment.NewLine}"
            + "This removes the Markdown file from disk and cannot be undone.",
            "Delete note",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation == MessageBoxResult.Yes)
        {
            await ViewModel.DeleteSelectedNoteAsync();
        }
    }

    private void AssignTags_OnClick(object sender, RoutedEventArgs e)
    {
        var context = ViewModel.CreateTagAssignmentContext();
        if (context is null)
        {
            ViewModel.StatusText =
                "Select a Markdown note from the current folder before assigning tags";
            return;
        }

        var dialog = new AssignTagsWindow(context)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            ViewModel.ApplyTagsToSelectedNote(dialog.SelectedTags);
        }
    }

    private void MainWindow_OnClosing(
        object? sender,
        CancelEventArgs e)
    {
        if (ViewModel.TrySaveSelectedNote(updateSearchIndex: false))
        {
            return;
        }

        e.Cancel = true;
        MessageBox.Show(
            this,
            $"{ViewModel.StatusText}{Environment.NewLine}{Environment.NewLine}"
            + "The application will remain open so the note is not lost.",
            "Could not save note",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            ViewModel.NewNoteCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            await OpenFolderAsync();
            e.Handled = true;
        }
    }

    private sealed record PdfDropTarget(
        NoteItem? Note,
        int? InsertionIndex);
}
