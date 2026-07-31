using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NoteManager.App.Infrastructure;
using NoteManager.App.Services;
using NoteManager.App.ViewModels;
using NoteManager.Desktop.Dialogs;

namespace NoteManager.Desktop;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType PdfFileType = new("PDF documents")
    {
        Patterns = ["*.pdf"],
        MimeTypes = ["application/pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"]
    };

    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteManager",
        "last-folder.txt");

    private readonly ApplicationOptions _options;
    private ShareDialog? _shareDialog;
    private UiAutomationServer? _automationServer;
    private bool _started;
    private bool _storagePickerOpen;

    public MainWindow()
        : this(new ApplicationOptions(null, null, null))
    {
    }

    public MainWindow(ApplicationOptions options)
    {
        _options = options;
        InitializeComponent();
        DataContext = new MainViewModel(
            new InfostackerPublishingService(baseUri: options.InfostackerBaseUri));
        Opened += MainWindow_OnOpened;
        Closing += MainWindow_OnClosing;
        KeyDown += MainWindow_OnKeyDown;
        DragDrop.AddDragOverHandler(this, MainWindow_OnDragOver);
        DragDrop.AddDropHandler(this, MainWindow_OnDrop);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        StartAutomationServer();
        var folder = _options.FolderPath ?? ReadLastFolder();
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            await LoadFolderAsync(folder);
        }
        else
        {
            ViewModel.StatusText =
                "Open a Markdown folder to begin. Sample documents are shown until then.";
        }
    }

    private async void OpenFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await LoadFolderAsync(folder);
        }
    }

    private async Task LoadFolderAsync(string folder)
    {
        await ViewModel.LoadMarkdownFolderAsync(folder);
        if (ViewModel.IsFolderMode
            && Path.GetFullPath(folder).Equals(
                ViewModel.CurrentFolderPath,
                StringComparison.Ordinal))
        {
            SaveLastFolder(ViewModel.CurrentFolderPath);
        }
    }

    private async void ImportPdf_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_storagePickerOpen)
        {
            return;
        }

        var note = ViewModel.SelectedNote;
        if (note is null || !ViewModel.CanImportPdfIntoNote(note))
        {
            ViewModel.StatusText = "Select a Markdown note before importing a PDF";
            return;
        }

        string[] paths;
        _storagePickerOpen = true;
        try
        {
            var results = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Import PDF",
                    AllowMultiple = true,
                    FileTypeFilter = [PdfFileType]
                });
            paths = results
                .Select(file => file.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();
        }
        finally
        {
            _storagePickerOpen = false;
        }

        if (paths.Length > 0)
        {
            await ViewModel.ImportPdfFilesAsync(
                note,
                paths,
                MarkdownEditor.CaretIndex);
        }
    }

    private void MainWindow_OnDragOver(object? sender, DragEventArgs e)
    {
        var hasPdf = e.DataTransfer.TryGetFiles()?
            .Select(file => file.TryGetLocalPath())
            .Any(path => path is not null
                         && Path.GetExtension(path).Equals(
                             ".pdf",
                             StringComparison.OrdinalIgnoreCase)) == true;
        e.DragEffects = hasPdf
                        && ViewModel.CanImportPdfIntoNote(ViewModel.SelectedNote)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void MainWindow_OnDrop(object? sender, DragEventArgs e)
    {
        var note = ViewModel.SelectedNote;
        var paths = e.DataTransfer.TryGetFiles()?
            .Select(file => file.TryGetLocalPath())
            .Where(path => path is not null
                           && Path.GetExtension(path).Equals(
                               ".pdf",
                               StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (note is null
            || paths.Length == 0
            || !ViewModel.CanImportPdfIntoNote(note))
        {
            ViewModel.StatusText =
                "Drop PDF files while a Markdown note is selected";
            return;
        }

        e.Handled = true;
        await ViewModel.ImportPdfFilesAsync(
            note,
            paths,
            MarkdownEditor.CaretIndex);
    }

    private async void Tags_OnClick(object? sender, RoutedEventArgs e)
    {
        var context = ViewModel.CreateTagAssignmentContext();
        if (context is null)
        {
            ViewModel.StatusText = "Select a Markdown note before assigning tags";
            return;
        }

        var dialog = new AssignTagsDialog(context);
        if (await dialog.ShowDialog<bool>(this))
        {
            ViewModel.ApplyTagsToSelectedNote(dialog.SelectedTags);
        }
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        var note = ViewModel.SelectedNote;
        if (note is null || !ViewModel.CanDeleteSelectedNote)
        {
            ViewModel.StatusText = "Select a Markdown note before deleting";
            return;
        }

        var dialog = new ConfirmDialog(
            "Delete note",
            $"Permanently delete “{note.FileName}”?\n\n"
            + "This removes the Markdown file from disk and cannot be undone.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.DeleteSelectedNoteAsync();
        }
    }

    private async void Share_OnClick(object? sender, RoutedEventArgs e)
    {
        await ShowShareDialogAsync();
    }

    private async Task ShowShareDialogAsync()
    {
        if (_shareDialog is not null || !ViewModel.CanPublishSelectedNote)
        {
            return;
        }

        var dialog = new ShareDialog(ViewModel);
        _shareDialog = dialog;
        ViewModel.IsSharePanelOpen = true;
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            _shareDialog = null;
            ViewModel.IsSharePanelOpen = false;
        }
    }

    private void Find_OnClick(object? sender, RoutedEventArgs e) => FocusSearch();

    private void SearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !ViewModel.IsSearchAvailable)
        {
            return;
        }

        ViewModel.SearchText = SearchBox.Text ?? string.Empty;
        ViewModel.SubmitSearch();
        e.Handled = true;
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void Exit_OnClick(object? sender, RoutedEventArgs e) => Close();

    private async void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        var commandModifier =
            e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!commandModifier)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F:
                FocusSearch();
                e.Handled = true;
                break;
            case Key.N:
                ViewModel.NewNoteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.O:
                e.Handled = true;
                var folder = await PickFolderAsync();
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    await LoadFolderAsync(folder);
                }
                break;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        if (_storagePickerOpen)
        {
            return null;
        }

        _storagePickerOpen = true;
        try
        {
            var results = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Open Markdown notes folder",
                    AllowMultiple = false
                });
            return results.FirstOrDefault()?.TryGetLocalPath();
        }
        finally
        {
            _storagePickerOpen = false;
        }
    }

    private void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!ViewModel.TrySaveSelectedNote(updateSearchIndex: false))
        {
            e.Cancel = true;
            ViewModel.StatusText =
                "The window remains open because the current note could not be saved.";
            return;
        }

        _automationServer?.Dispose();
        _automationServer = null;
        ViewModel.Dispose();
    }

    private void StartAutomationServer()
    {
        if (_options.AutomationPipeName is null)
        {
            return;
        }

        _automationServer = new UiAutomationServer(
            _options.AutomationPipeName,
            (folder, cancellationToken) => RunOnUiThreadAsync(
                () => LoadFolderAsync(folder),
                cancellationToken),
            (pdfPath, cancellationToken) => RunOnUiThreadAsync(
                async () =>
                {
                    var note = ViewModel.SelectedNote
                        ?? throw new InvalidOperationException(
                            "Select a Markdown note before importing a PDF.");
                    await ViewModel.ImportPdfFilesAsync(
                        note,
                        [pdfPath],
                        MarkdownEditor.CaretIndex);
                },
                cancellationToken),
            cancellationToken => RunOnUiThreadAsync(
                () =>
                {
                    _ = ShowShareDialogAsync();
                    return Task.CompletedTask;
                },
                cancellationToken));
        _automationServer.Start();
    }

    private static async Task RunOnUiThreadAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatched = await Dispatcher.UIThread.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken);
        await dispatched;
    }

    private static string? ReadLastFolder()
    {
        try
        {
            return File.Exists(StateFilePath)
                ? File.ReadAllText(StateFilePath).Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLastFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            File.WriteAllText(StateFilePath, folder);
        }
        catch
        {
            // Remembering the folder is a convenience; failure is non-fatal.
        }
    }
}
