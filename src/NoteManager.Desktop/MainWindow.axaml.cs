using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NoteManager.App.Infrastructure;
using NoteManager.App.Models;
using NoteManager.App.Services;
using NoteManager.App.ViewModels;
using NoteManager.Desktop.Dialogs;
using NoteManager.Desktop.Services;
using NoteManager.Plugins;

namespace NoteManager.Desktop;

public partial class MainWindow : Window
{
    private enum FolderOpenSource
    {
        Other,
        UserSelection,
        PreviousSession
    }

    private static readonly FilePickerFileType PdfFileType = new("PDF documents")
    {
        Patterns = ["*.pdf"],
        MimeTypes = ["application/pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"]
    };

    private readonly ApplicationOptions _options;
    private readonly LastOpenedFolderService _lastOpenedFolderService = new();
    private readonly ApplicationActivityLog _activityLog = new();
    private readonly PluginManager _pluginManager;
    private ShareDialog? _shareDialog;
    private UiAutomationServer? _automationServer;
    private bool _started;
    private bool _storagePickerOpen;
    private bool _isCommittingTitleEdit;
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;
    private NoteItem? _titleEditNote;

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
        _pluginManager = new PluginManager(
            AppContext.BaseDirectory,
            SaveActiveNoteForPluginAsync,
            ReportPluginStatus,
            ReportPluginIndicatorStatus,
            ReportPluginIndicatorVisibility);
        Opened += MainWindow_OnOpened;
        Closing += MainWindow_OnClosing;
        KeyDown += MainWindow_OnKeyDown;
        NoteScrollViewer.AddHandler(
            PointerWheelChangedEvent,
            NoteScrollViewer_OnPointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        DragDrop.AddDragOverHandler(this, MainWindow_OnDragOver);
        DragDrop.AddDropHandler(this, MainWindow_OnDrop);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private void NoteScrollViewer_OnPointerWheelChanged(
        object? sender,
        PointerWheelEventArgs e)
    {
        if (NoteScrollCoordinator.IsModifiedGesture(e.KeyModifiers)
            || e.Delta.Y == 0)
        {
            return;
        }

        if (NoteScrollCoordinator.ScrollBy(
                NoteScrollViewer,
                -e.Delta.Y,
                ScrollDeltaMode.Line))
        {
            e.Handled = true;
        }
    }

    private void SelectedNoteTitle_OnClick(
        object? sender,
        RoutedEventArgs e)
    {
        var note = ViewModel.SelectedNote;
        if (note is null || !ViewModel.CanDeleteSelectedNote)
        {
            return;
        }

        _titleEditNote = note;
        SelectedNoteTitleEditor.Text = note.FileName;
        SelectedNoteTitle.IsVisible = false;
        SelectedNoteTitleEditor.IsVisible = true;
        SelectedNoteTitleEditor.Focus();
        SelectedNoteTitleEditor.SelectAll();
        e.Handled = true;
    }

    private void SelectedNoteTitleEditor_OnKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTitleEdit(keepOpenOnFailure: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndTitleEdit();
            e.Handled = true;
        }
    }

    private void SelectedNoteTitleEditor_OnLostFocus(
        object? sender,
        RoutedEventArgs e)
    {
        CommitTitleEdit(keepOpenOnFailure: false);
    }

    private void CommitTitleEdit(bool keepOpenOnFailure)
    {
        if (_isCommittingTitleEdit || _titleEditNote is null)
        {
            return;
        }

        _isCommittingTitleEdit = true;
        try
        {
            if (ViewModel.TryRenameNote(
                    _titleEditNote,
                    SelectedNoteTitleEditor.Text ?? string.Empty))
            {
                EndTitleEdit();
            }
            else if (keepOpenOnFailure)
            {
                SelectedNoteTitleEditor.Focus();
                SelectedNoteTitleEditor.SelectAll();
            }
            else
            {
                EndTitleEdit();
            }
        }
        finally
        {
            _isCommittingTitleEdit = false;
        }
    }

    private void EndTitleEdit()
    {
        _titleEditNote = null;
        SelectedNoteTitleEditor.IsVisible = false;
        SelectedNoteTitle.IsVisible = true;
    }

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            StartAutomationServer();
            _activityLog.TryWriteApplicationOpened();
            var folderFromPreviousSession = _options.FolderPath is null
                                            ? _lastOpenedFolderService.ReadExistingFolder()
                                            : null;
            var folder = _options.FolderPath ?? folderFromPreviousSession;
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                await LoadFolderAsync(
                    folder,
                    folderFromPreviousSession is not null
                        ? FolderOpenSource.PreviousSession
                        : FolderOpenSource.Other);
            }
            else
            {
                await RequireFolderSelectionAsync();
            }
        }
        catch (Exception exception)
        {
            ReportUnhandled("MainWindow.Opened", exception);
        }
    }

    private async void OpenFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await PickFolderAsync();
            if (!string.IsNullOrWhiteSpace(folder))
            {
                await LoadFolderAsync(folder, FolderOpenSource.UserSelection);
            }
        }
        catch (Exception exception)
        {
            ReportUnhandled("MainWindow.OpenFolder", exception);
        }
    }

    private async Task LoadFolderAsync(
        string folder,
        FolderOpenSource source = FolderOpenSource.Other)
    {
        try
        {
            await ViewModel.LoadMarkdownFolderAsync(folder);
            if (ViewModel.IsFolderMode
                && Path.GetFullPath(folder).Equals(
                    ViewModel.CurrentFolderPath,
                    StringComparison.Ordinal))
            {
                _lastOpenedFolderService.TrySave(ViewModel.CurrentFolderPath);
                if (source == FolderOpenSource.UserSelection)
                {
                    _activityLog.TryWriteFolderSelected(ViewModel.CurrentFolderPath);
                }
                else if (source == FolderOpenSource.PreviousSession)
                {
                    _activityLog.TryWriteFolderRestoredFromPreviousSession(
                        ViewModel.CurrentFolderPath);
                }
                ViewModel.GitStatusText = "GIT synced";
                await _pluginManager.SetVaultAsync(ViewModel.CurrentFolderPath);
            }
        }
        catch (Exception exception)
        {
            ReportUnhandled("folder open", exception);
        }
    }

    private void ReportUnhandled(string source, Exception exception)
    {
        _activityLog.TryWriteUnhandledException(source, exception);
        ViewModel.StatusText = $"NoteManager hit an unexpected error: {exception.Message}";
    }

    private async Task RequireFolderSelectionAsync()
    {
        ViewModel.StatusText = "Select a Markdown folder to begin.";
        var folder = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            ViewModel.StatusText =
                "A Markdown folder must be selected before you can work with notes.";
            return;
        }

        await LoadFolderAsync(folder, FolderOpenSource.UserSelection);
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

    private async void Plugins_OnClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new PluginsDialog(_pluginManager);
        await dialog.ShowDialog(this);
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
        if (!IsCommandShortcut(e.KeyModifiers))
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
                    await LoadFolderAsync(folder, FolderOpenSource.UserSelection);
                }
                break;
        }
    }

    private static bool IsCommandShortcut(KeyModifiers modifiers)
    {
        // AltGr is reported as Ctrl+Alt on Windows.  Excluding Alt ensures
        // Polish characters such as AltGr+O (ó) are handled by the editor.
        return !modifiers.HasFlag(KeyModifiers.Alt)
               && (modifiers.HasFlag(KeyModifiers.Control)
                   || modifiers.HasFlag(KeyModifiers.Meta));
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

    private async void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        if (!ViewModel.TrySaveSelectedNote(updateSearchIndex: false))
        {
            ViewModel.StatusText =
                "The window remains open because the current note could not be saved.";
            return;
        }

        _shutdownInProgress = true;
        _automationServer?.Dispose();
        _automationServer = null;
        try
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await _pluginManager.StopAllAsync(stopTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Shutdown remains bounded even when an external Git process is slow.
            }

            ViewModel.Dispose();
            _shutdownCompleted = true;
            Close();
        }
        finally
        {
            _shutdownInProgress = false;
        }
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

    private async Task<bool> SaveActiveNoteForPluginAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(
            () => ViewModel.TrySaveSelectedNote(updateSearchIndex: false),
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private void ReportPluginStatus(string message)
        => Dispatcher.UIThread.Post(
            () => ViewModel.StatusText = message,
            DispatcherPriority.Normal);

    private void ReportPluginIndicatorStatus(PluginIndicatorStatus status)
    {
        if (!status.PluginId.Equals("git-integration", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => ViewModel.GitStatusText = status.Text,
            DispatcherPriority.Normal);
    }

    private void ReportPluginIndicatorVisibility(string pluginId, bool isVisible)
    {
        if (!pluginId.Equals("git-integration", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => ViewModel.IsGitStatusVisible = isVisible,
            DispatcherPriority.Normal);
    }

}
