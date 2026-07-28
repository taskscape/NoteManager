using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using NoteManager.App.Infrastructure;
using NoteManager.App.Models;
using NoteManager.App.Services;

namespace NoteManager.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly RangeObservableCollection<NoteItem> _allNotes;
    private readonly RangeObservableCollection<NoteItem> _visibleNotes;
    private readonly InfostackerPublishingService _infostackerPublishingService;
    private CancellationTokenSource? _indexCancellation;
    private CancellationTokenSource? _mediaRefreshCancellation;
    private CancellationTokenSource? _publishCancellation;
    private CancellationTokenSource? _searchCancellation;
    private HashSet<string>? _fullTextMatches;
    private string _fullTextMatchQuery = string.Empty;
    private string _searchText = string.Empty;
    private NoteItem? _selectedNote;
    private NavigationItem? _selectedNavigationItem;
    private bool _isSharePanelOpen;
    private bool _isSynced;
    private bool _isFolderMode;
    private bool _isLoadingFolder;
    private bool _isIndexing;
    private bool _isPublishing;
    private string _statusText = string.Empty;
    private string _centerHeading = "All notes";
    private string _currentFolderPath = string.Empty;
    private string _searchIndexStatus = string.Empty;
    private string _shareStatusText = string.Empty;
    private EmbeddedMediaVaultIndex? _mediaIndex;
    private long _folderGeneration;

    private const string AllNotesFilterKey = "*";
    private const string UntaggedFilterKey = "__untagged__";

    public MainViewModel(
        InfostackerPublishingService? infostackerPublishingService = null)
    {
        _infostackerPublishingService =
            infostackerPublishingService ?? new InfostackerPublishingService();
        _allNotes = new RangeObservableCollection<NoteItem>();
        _visibleNotes = new RangeObservableCollection<NoteItem>();
        _allNotes.ReplaceRange(SampleDataService.CreateNotes());
        NavigationItems = [];
        RebuildTagNavigation();

        NewNoteCommand = new AsyncRelayCommand(_ => CreateNewNoteAsync(), _ => CanCreateNote);
        ClearTagFilterCommand = new RelayCommand(_ => ClearTagFilter());
        SyncCommand = new RelayCommand(_ => Sync());
        ShareCommand = new RelayCommand(_ => ToggleSharePanel());
        CloseShareCommand = new RelayCommand(_ => IsSharePanelOpen = false);
        OpenAttachmentCommand = new RelayCommand(_ => OpenAttachment(), _ => SelectedNote is not null);
        ViewModeCommand = new RelayCommand(
            parameter => ChangeView(parameter?.ToString() ?? "View updated"));

        _visibleNotes.ReplaceRange(_allNotes);
        SelectedNote = _allNotes.First(note => note.ThumbnailKind == ThumbnailKind.SpacePoster);
        SampleDocumentService.EnsureAll(_allNotes);
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }
    public ObservableCollection<NoteItem> NotesView => _visibleNotes;

    public AsyncRelayCommand NewNoteCommand { get; }
    public RelayCommand ClearTagFilterCommand { get; }
    public RelayCommand SyncCommand { get; }
    public RelayCommand ShareCommand { get; }
    public RelayCommand CloseShareCommand { get; }
    public RelayCommand OpenAttachmentCommand { get; }
    public RelayCommand ViewModeCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _fullTextMatches = null;
                _fullTextMatchQuery = string.Empty;
                RefreshNoteFilter();
                QueueFullTextSearch(useDebounce: true);
            }
        }
    }

    public string CenterHeading
    {
        get => _centerHeading;
        private set => SetProperty(ref _centerHeading, value);
    }

    public string VisibleNoteCount => $"{NotesView.Count:N0} notes";

    public NoteItem? SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (!ReferenceEquals(value, _selectedNote)
                && !TrySaveSelectedNote())
            {
                OnPropertyChanged(nameof(SelectedNote));
                return;
            }

            if (!ReferenceEquals(value, _selectedNote))
            {
                CancelPublishing();
                ShareStatusText = string.Empty;
            }

            if (value is { IsMarkdownFile: true, IsContentLoaded: false })
            {
                try
                {
                    value.LoadPlainTextContent(
                        File.ReadAllText(value.SourceFilePath));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    value.LoadPlainTextContent(
                        $"The Markdown file could not be read.{Environment.NewLine}{exception.Message}");
                }
            }

            var previousNote = _selectedNote;
            if (SetProperty(ref _selectedNote, value))
            {
                DetachSelectedNote(previousNote);
                AttachSelectedNote(value);
                OnPropertyChanged(nameof(CanDeleteSelectedNote));
                OnPropertyChanged(nameof(CanPublishSelectedNote));
                OnPropertyChanged(nameof(CanAssignTags));
                OpenAttachmentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public NavigationItem? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (!ReferenceEquals(value, _selectedNavigationItem)
                && !TrySaveSelectedNote())
            {
                OnPropertyChanged(nameof(SelectedNavigationItem));
                return;
            }

            if (!SetProperty(ref _selectedNavigationItem, value))
            {
                return;
            }

            CenterHeading = value?.Label ?? "All notes";
            RefreshNoteFilter();
        }
    }

    public bool IsSharePanelOpen
    {
        get => _isSharePanelOpen;
        set => SetProperty(ref _isSharePanelOpen, value);
    }

    public bool IsPublishing
    {
        get => _isPublishing;
        private set
        {
            if (SetProperty(ref _isPublishing, value))
            {
                OnPropertyChanged(nameof(ShareActionText));
                OnPropertyChanged(nameof(CanPublishSelectedNote));
            }
        }
    }

    public string ShareStatusText
    {
        get => _shareStatusText;
        private set => SetProperty(ref _shareStatusText, value);
    }

    public string ShareActionText
        => IsPublishing
            ? "Publishing…"
            : "Publish and copy public link";

    public bool IsSynced
    {
        get => _isSynced;
        set => SetProperty(ref _isSynced, value);
    }

    public bool IsFolderMode
    {
        get => _isFolderMode;
        private set
        {
            if (SetProperty(ref _isFolderMode, value))
            {
                RefreshFileCommandState();
            }
        }
    }

    public bool IsLoadingFolder
    {
        get => _isLoadingFolder;
        private set
        {
            if (SetProperty(ref _isLoadingFolder, value))
            {
                RefreshFileCommandState();
            }
        }
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set => SetProperty(ref _isIndexing, value);
    }

    public string CurrentFolderPath
    {
        get => _currentFolderPath;
        private set
        {
            if (SetProperty(ref _currentFolderPath, value))
            {
                RefreshFileCommandState();
            }
        }
    }

    public string SearchIndexStatus
    {
        get => _searchIndexStatus;
        private set => SetProperty(ref _searchIndexStatus, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool CanCreateNote
        => IsFolderMode
           && !IsLoadingFolder
           && Directory.Exists(CurrentFolderPath);

    public bool CanDeleteSelectedNote
        => CanCreateNote
           && SelectedNote is not null
           && IsMarkdownPathInCurrentFolder(SelectedNote.SourceFilePath);

    public bool CanPublishSelectedNote
        => CanDeleteSelectedNote && !IsPublishing;

    public bool CanAssignTags
        => CanDeleteSelectedNote;

    public TagAssignmentContext? CreateTagAssignmentContext()
    {
        var note = SelectedNote;
        return note is null || !CanAssignTags
            ? null
            : TagAssignmentService.CreateContext(_allNotes, note);
    }

    public bool ApplyTagsToSelectedNote(IEnumerable<string> selectedTags)
    {
        ArgumentNullException.ThrowIfNull(selectedTags);

        var note = SelectedNote;
        if (note is null || !CanAssignTags)
        {
            SetStatus("Select a Markdown note from the current folder before assigning tags");
            return false;
        }

        var normalizedTags = MarkdownTagEditorService.NormalizeTags(selectedTags);
        var updatedMarkdown = MarkdownTagEditorService.RewriteTagBlocks(
            note.PlainTextContent,
            normalizedTags);
        var selectedFilterKey = SelectedNavigationItem?.FilterKey;

        note.PlainTextContent = updatedMarkdown;
        note.ReplaceTags(normalizedTags);
        if (!TrySaveSelectedNote())
        {
            return false;
        }

        RebuildTagNavigation();
        var restoredNavigation = NavigationItems.FirstOrDefault(item =>
            item.FilterKey.Equals(
                selectedFilterKey ?? AllNotesFilterKey,
                StringComparison.OrdinalIgnoreCase))
            ?? NavigationItems.FirstOrDefault(item =>
                item.FilterKey == AllNotesFilterKey);
        SelectedNavigationItem = restoredNavigation;
        RefreshNoteFilter();
        SetStatus(
            normalizedTags.Length == 1
                ? $"Assigned 1 tag to {note.FileName}"
                : $"Assigned {normalizedTags.Length:N0} tags to {note.FileName}");
        return true;
    }

    private bool FilterNote(NoteItem note)
    {
        var selectedTag = SelectedNavigationItem?.FilterKey;
        if (selectedTag == UntaggedFilterKey && note.Tags.Length > 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(selectedTag)
            && selectedTag != AllNotesFilterKey
            && selectedTag != UntaggedFilterKey
            && !note.Tags.Any(tag => tag.Equals(selectedTag, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        var metadataMatch = MatchesMetadata(note, query);
        if (!IsFolderMode
            || _fullTextMatches is null
            || !_fullTextMatchQuery.Equals(query, StringComparison.Ordinal))
        {
            return metadataMatch;
        }

        return _fullTextMatches.Contains(note.SourceFilePath)
               || (IsIndexing && metadataMatch);
    }

    private static bool MatchesMetadata(NoteItem note, string query)
        => note.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
           || note.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)
           || note.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
           || note.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void RefreshNoteFilter()
    {
        _visibleNotes.ReplaceRange(_allNotes.Where(FilterNote));
        EnsureSelectedNote();
        OnPropertyChanged(nameof(VisibleNoteCount));
    }

    private void EnsureSelectedNote()
    {
        if (SelectedNote is not null && NotesView.Contains(SelectedNote))
        {
            return;
        }

        SelectedNote = NotesView.FirstOrDefault();
    }

    private async Task CreateNewNoteAsync()
    {
        if (!CanCreateNote)
        {
            SetStatus("Open a notes folder before creating a note");
            return;
        }

        var folderPath = CurrentFolderPath;
        try
        {
            var createdPath = await Task.Run(() => CreateEmptyMarkdownFile(folderPath));
            await LoadMarkdownFolderAsync(folderPath, createdPath);
            SetStatus($"Created {Path.GetFileName(createdPath)}");
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            SetStatus($"Could not create note: {exception.Message}");
        }
    }

    public async Task<bool> DeleteSelectedNoteAsync()
    {
        var note = SelectedNote;
        if (note is null || !CanDeleteSelectedNote)
        {
            SetStatus("Select a Markdown note from the current folder before deleting");
            return false;
        }

        var folderPath = CurrentFolderPath;
        var filePath = Path.GetFullPath(note.SourceFilePath);
        var fileName = Path.GetFileName(filePath);

        try
        {
            await Task.Run(() => File.Delete(filePath));
            note.MarkSaved();
            await LoadMarkdownFolderAsync(folderPath);
            SetStatus($"Deleted {fileName}");
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            SetStatus($"Could not delete note: {exception.Message}");
            return false;
        }
    }

    public bool CanImportPdfIntoNote(NoteItem? note)
        => note is { IsMarkdownFile: true }
           && IsFolderMode
           && IsMarkdownPathInCurrentFolder(note.SourceFilePath);

    public async Task ImportPdfFilesAsync(
        NoteItem targetNote,
        IReadOnlyList<string> sourcePaths,
        int? insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(targetNote);
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var pdfPaths = sourcePaths
            .Where(path => File.Exists(path)
                           && Path.GetExtension(path).Equals(
                               ".pdf",
                               StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pdfPaths.Length == 0)
        {
            SetStatus("Drop one or more PDF files to embed them in a note");
            return;
        }

        if (!CanImportPdfIntoNote(targetNote))
        {
            SetStatus("Select a Markdown note inside the open folder before dropping a PDF");
            return;
        }

        if (!ReferenceEquals(SelectedNote, targetNote))
        {
            SelectedNote = targetNote;
            if (!ReferenceEquals(SelectedNote, targetNote))
            {
                return;
            }
        }

        var folderPath = CurrentFolderPath;
        var folderGeneration = _folderGeneration;
        SetStatus(
            pdfPaths.Length == 1
                ? $"Importing {Path.GetFileName(pdfPaths[0])}…"
                : $"Importing {pdfPaths.Length:N0} PDF files…");

        try
        {
            var importedPdfs = await Task.Run(
                () => ImportPdfFiles(
                    pdfPaths,
                    folderPath,
                    targetNote.SourceFilePath));
            if (folderGeneration != _folderGeneration
                || !ReferenceEquals(SelectedNote, targetNote))
            {
                DeleteCopiedImports(importedPdfs);
                return;
            }

            targetNote.PlainTextContent = PdfDropImportService.InsertMarkdownEmbeds(
                targetNote.PlainTextContent,
                importedPdfs.Select(pdf => pdf.MarkdownEmbed),
                insertionIndex);
            targetNote.AddEmbeddedMediaReferences(
                importedPdfs.Select(pdf => new EmbeddedMediaReference(
                    pdf.EmbedTarget,
                    pdf.DestinationPath,
                    EmbeddedMediaKind.Pdf)));

            if (!TrySaveSelectedNote())
            {
                return;
            }

            var copiedCount = importedPdfs.Count(pdf => pdf.WasCopied);
            var copySummary = copiedCount == 0
                ? string.Empty
                : $" · {copiedCount:N0} copied to the folder root";
            SetStatus(
                $"Embedded {importedPdfs.Length:N0} PDF file(s) in {targetNote.FileName}{copySummary}");
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            SetStatus($"Could not embed the dropped PDF: {exception.Message}");
        }
    }

    private static ImportedPdf[] ImportPdfFiles(
        IEnumerable<string> sourcePaths,
        string folderPath,
        string markdownFilePath)
    {
        var importedPdfs = new List<ImportedPdf>();
        try
        {
            foreach (var sourcePath in sourcePaths)
            {
                importedPdfs.Add(
                    PdfDropImportService.Import(
                        sourcePath,
                        folderPath,
                        markdownFilePath));
            }

            return importedPdfs.ToArray();
        }
        catch
        {
            DeleteCopiedImports(importedPdfs);
            throw;
        }
    }

    private static void DeleteCopiedImports(IEnumerable<ImportedPdf> importedPdfs)
    {
        foreach (var importedPdf in importedPdfs.Where(pdf => pdf.WasCopied))
        {
            try
            {
                if (File.Exists(importedPdf.DestinationPath))
                {
                    File.Delete(importedPdf.DestinationPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort rollback must not hide the original import outcome.
            }
        }
    }

    public async Task<string?> PublishSelectedNoteAsync()
    {
        var note = SelectedNote;
        if (note is null
            || !CanPublishSelectedNote)
        {
            ShareStatusText =
                "Select a Markdown note from the current folder before publishing.";
            return null;
        }

        if (!TrySaveSelectedNote())
        {
            ShareStatusText = StatusText;
            return null;
        }

        CancelPublishing();
        _publishCancellation = new CancellationTokenSource();
        var cancellationToken = _publishCancellation.Token;
        var folderPath = CurrentFolderPath;

        IsPublishing = true;
        ShareStatusText = $"Publishing {note.FileName}…";
        try
        {
            var publicUrl = await _infostackerPublishingService.PublishAsync(
                note,
                folderPath,
                cancellationToken);
            ShareStatusText = "Published. Copying public link…";
            return publicUrl;
        }
        catch (OperationCanceledException)
        {
            ShareStatusText = "Publishing cancelled.";
            return null;
        }
        catch (InfostackerPublishingException exception)
        {
            ShareStatusText = exception.Message;
            SetStatus(exception.Message);
            return null;
        }
        finally
        {
            IsPublishing = false;
            _publishCancellation?.Dispose();
            _publishCancellation = null;
        }
    }

    public void ConfirmPublicLinkCopied(string publicUrl)
    {
        ShareStatusText = "Public link copied to the clipboard.";
        SetStatus($"Published note to Infostacker: {publicUrl}");
    }

    public void ReportClipboardFailure(string message)
    {
        ShareStatusText =
            $"The note was published, but the public link could not be copied: {message}";
        SetStatus("Note published, but copying the public link failed");
    }

    private static string CreateEmptyMarkdownFile(string folderPath)
    {
        for (var suffix = 1; suffix <= 10_000; suffix++)
        {
            var fileName = suffix == 1
                ? "Untitled note.md"
                : $"Untitled note {suffix}.md";
            var candidatePath = Path.Combine(folderPath, fileName);

            try
            {
                using var stream = new FileStream(
                    candidatePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
                return candidatePath;
            }
            catch (IOException) when (File.Exists(candidatePath))
            {
                // Try the next available deterministic file name.
            }
        }

        throw new IOException("No available Untitled note file name was found.");
    }

    public bool TrySaveSelectedNote(bool updateSearchIndex = true)
    {
        var note = SelectedNote;
        if (note is not { IsMarkdownFile: true, IsDirty: true })
        {
            return true;
        }

        if (!IsMarkdownPathInCurrentFolder(note.SourceFilePath))
        {
            SetStatus("The current note cannot be saved outside the selected folder");
            return false;
        }

        try
        {
            WriteTextAtomically(
                note.SourceFilePath,
                note.PlainTextContent);
            note.MarkSaved();
            RefreshEmbeddedMediaReferences(note);
            SetStatus($"Saved {note.FileName}");
            if (updateSearchIndex)
            {
                StartBackgroundIndex(CurrentFolderPath);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            SetStatus(
                $"Could not save {note.FileName}; the current note remains open: {exception.Message}");
            return false;
        }
    }

    private static void WriteTextAtomically(
        string filePath,
        string content)
    {
        var fullPath = Path.GetFullPath(filePath);
        var folderPath = Path.GetDirectoryName(fullPath)
                         ?? throw new IOException(
                             "The note folder could not be resolved.");
        var temporaryPath = Path.Combine(
            folderPath,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true);
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(
                temporaryPath,
                fullPath,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A failed cleanup must not hide the original save outcome.
            }
        }
    }

    private bool IsMarkdownPathInCurrentFolder(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)
            || string.IsNullOrWhiteSpace(CurrentFolderPath)
            || !Path.GetExtension(filePath).Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(CurrentFolderPath),
            Path.GetFullPath(filePath));
        return !Path.IsPathRooted(relativePath)
               && !relativePath.Equals("..", StringComparison.Ordinal)
               && !relativePath.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private void RefreshFileCommandState()
    {
        OnPropertyChanged(nameof(CanCreateNote));
        OnPropertyChanged(nameof(CanDeleteSelectedNote));
        OnPropertyChanged(nameof(CanPublishSelectedNote));
        OnPropertyChanged(nameof(CanAssignTags));
        NewNoteCommand.RaiseCanExecuteChanged();
    }

    private void ToggleSharePanel()
    {
        IsSharePanelOpen = !IsSharePanelOpen;
        if (IsSharePanelOpen && !CanPublishSelectedNote)
        {
            ShareStatusText =
                "Select a Markdown note from the current folder before publishing.";
        }
    }

    private void CancelPublishing()
    {
        _publishCancellation?.Cancel();
        _publishCancellation?.Dispose();
        _publishCancellation = null;
    }

    private void ChangeView(string status)
    {
        if (TrySaveSelectedNote())
        {
            SetStatus(status);
        }
    }

    private void ClearTagFilter()
    {
        SelectedNavigationItem = IsFolderMode
            ? NavigationItems.FirstOrDefault(item => item.FilterKey == AllNotesFilterKey)
            : null;
    }

    private void RebuildTagNavigation()
    {
        var tags = _allNotes
            .SelectMany(note => note.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new NavigationItem
            {
                Label = group.Key,
                Count = $"({group.Count():N0})",
                FilterKey = group.Key
            })
            .ToArray();

        NavigationItems.Clear();
        if (IsFolderMode)
        {
            NavigationItems.Add(new NavigationItem
            {
                Label = "All notes",
                Count = $"({_allNotes.Count:N0})",
                FilterKey = AllNotesFilterKey
            });
            NavigationItems.Add(new NavigationItem
            {
                Label = "Untagged",
                Count = $"({_allNotes.Count(note => note.Tags.Length == 0):N0})",
                FilterKey = UntaggedFilterKey
            });
        }

        foreach (var tag in tags)
        {
            NavigationItems.Add(tag);
        }
    }

    public async Task LoadMarkdownFolderAsync(
        string folderPath,
        string? selectedFilePath = null)
    {
        if (!TrySaveSelectedNote(updateSearchIndex: false))
        {
            return;
        }

        CancelPublishing();
        IsLoadingFolder = true;
        SetStatus($"Loading Markdown notes from {folderPath}…");

        try
        {
            var result = await Task.Run(() => MarkdownFolderService.LoadFolder(folderPath));

            SearchText = string.Empty;
            SelectedNavigationItem = null;
            SelectedNote = null;
            _mediaIndex = result.MediaIndex;
            _allNotes.ReplaceRange(result.Notes);

            CurrentFolderPath = Path.GetFullPath(folderPath);
            IsFolderMode = true;
            RebuildTagNavigation();
            SelectedNavigationItem = NavigationItems.FirstOrDefault(item => item.FilterKey == AllNotesFilterKey);
            SelectedNote = selectedFilePath is null
                ? NotesView.FirstOrDefault()
                : NotesView
                    .FirstOrDefault(note => note.SourceFilePath.Equals(
                        selectedFilePath,
                        StringComparison.OrdinalIgnoreCase))
                  ?? NotesView.FirstOrDefault();
            OnPropertyChanged(nameof(VisibleNoteCount));

            var skippedText = result.FailedFileCount > 0
                ? $" ({result.FailedFileCount:N0} unreadable file(s) skipped)"
                : string.Empty;
            SetStatus($"Loaded {result.Notes.Count:N0} Markdown note(s) from {CurrentFolderPath}{skippedText}");
            StartBackgroundIndex(CurrentFolderPath);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not open folder: {exception.Message}");
        }
        finally
        {
            IsLoadingFolder = false;
        }
    }

    private void StartBackgroundIndex(string folderPath)
    {
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();

        _indexCancellation = new CancellationTokenSource();
        _searchCancellation = null;
        _fullTextMatches = null;
        _fullTextMatchQuery = string.Empty;
        var generation = ++_folderGeneration;
        var cancellationToken = _indexCancellation.Token;

        IsIndexing = true;
        SearchIndexStatus = "Indexing 0%";
        var progress = new Progress<NoteSearchIndexProgress>(update =>
        {
            if (generation != _folderGeneration || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var percentage = update.TotalFiles == 0
                ? 100
                : (int)Math.Round(update.ProcessedFiles * 100d / update.TotalFiles);
            SearchIndexStatus = $"Indexing {Math.Clamp(percentage, 0, 100)}%";
            SetStatus(
                $"Updating full-text index… {update.ProcessedFiles:N0} / {update.TotalFiles:N0} notes");
            QueueFullTextSearch(useDebounce: true);
        });

        _ = RunIndexUpdateAsync(folderPath, generation, progress, cancellationToken);
    }

    private async Task RunIndexUpdateAsync(
        string folderPath,
        long generation,
        IProgress<NoteSearchIndexProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(
                () => NoteSearchIndexService.UpdateIndex(folderPath, progress, cancellationToken));
            if (generation != _folderGeneration || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            IsIndexing = false;
            SearchIndexStatus = "Full-text ready";
            var changeSummary = result.UpdatedFiles == 0 && result.RemovedFiles == 0
                ? "no changes"
                : $"{result.UpdatedFiles:N0} updated, {result.RemovedFiles:N0} removed";
            var failureSummary = result.FailedFiles == 0
                ? string.Empty
                : $", {result.FailedFiles:N0} unreadable";
            SetStatus(
                $"Full-text index ready · {result.TotalFiles:N0} notes · {changeSummary}{failureSummary}");
            QueueFullTextSearch(useDebounce: false);
        }
        catch (OperationCanceledException)
        {
            // Switching folders and closing the window intentionally cancel indexing.
        }
        catch (Exception exception)
        {
            if (generation != _folderGeneration)
            {
                return;
            }

            IsIndexing = false;
            SearchIndexStatus = "Index unavailable";
            SetStatus($"Notes loaded; full-text index unavailable: {exception.Message}");
        }
    }

    private void QueueFullTextSearch(bool useDebounce)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;

        var query = SearchText.Trim();
        if (!IsFolderMode
            || string.IsNullOrWhiteSpace(CurrentFolderPath)
            || query.Length == 0)
        {
            return;
        }

        _searchCancellation = new CancellationTokenSource();
        var cancellationToken = _searchCancellation.Token;
        var folderPath = CurrentFolderPath;
        var generation = _folderGeneration;
        _ = RunFullTextSearchAsync(
            folderPath,
            query,
            generation,
            useDebounce,
            cancellationToken);
    }

    private async Task RunFullTextSearchAsync(
        string folderPath,
        string query,
        long generation,
        bool useDebounce,
        CancellationToken cancellationToken)
    {
        try
        {
            if (useDebounce)
            {
                await Task.Delay(250, cancellationToken);
            }

            var result = await Task.Run(
                () => NoteSearchIndexService.Search(
                    folderPath,
                    query,
                    maxResults: 10_000,
                    cancellationToken),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || generation != _folderGeneration
                || !SearchText.Trim().Equals(query, StringComparison.Ordinal))
            {
                return;
            }

            if (result.IsAvailable)
            {
                _fullTextMatches = result.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                _fullTextMatchQuery = query;
                RefreshNoteFilter();
            }
        }
        catch (OperationCanceledException)
        {
            // A later keystroke, folder switch, or shutdown superseded this query.
        }
    }

    public void Dispose()
    {
        TrySaveSelectedNote(updateSearchIndex: false);
        _folderGeneration++;
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _indexCancellation = null;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        _mediaRefreshCancellation?.Cancel();
        _mediaRefreshCancellation?.Dispose();
        _mediaRefreshCancellation = null;
        DetachSelectedNote(SelectedNote);
        CancelPublishing();
    }

    private void Sync()
    {
        IsSynced = true;
        SetStatus($"All changes synced at {DateTime.Now:HH:mm}");
    }

    private void OpenAttachment()
    {
        if (SelectedNote is null)
        {
            return;
        }

        var path = string.IsNullOrWhiteSpace(SelectedNote.GeneratedFilePath)
            ? SampleDocumentService.EnsureDocument(SelectedNote)
            : SelectedNote.GeneratedFilePath;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetStatus($"Opened {SelectedNote.FileName}");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not open file: {exception.Message}");
        }
    }

    private void SetStatus(string message) => StatusText = message;

    private void AttachSelectedNote(NoteItem? note)
    {
        if (note is null)
        {
            return;
        }

        note.PropertyChanged += SelectedNote_OnPropertyChanged;
        RefreshEmbeddedMediaReferences(note);
    }

    private void DetachSelectedNote(NoteItem? note)
    {
        if (note is not null)
        {
            note.PropertyChanged -= SelectedNote_OnPropertyChanged;
        }
    }

    private void SelectedNote_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteItem.PlainTextContent)
            && sender is NoteItem note
            && ReferenceEquals(note, SelectedNote))
        {
            QueueEmbeddedMediaRefresh(note);
        }
    }

    private void QueueEmbeddedMediaRefresh(NoteItem note)
    {
        _mediaRefreshCancellation?.Cancel();
        _mediaRefreshCancellation?.Dispose();
        _mediaRefreshCancellation = null;

        if (_mediaIndex is null || !CanResolveEmbeddedMediaFor(note))
        {
            return;
        }

        _mediaRefreshCancellation = new CancellationTokenSource();
        var cancellationToken = _mediaRefreshCancellation.Token;
        var generation = _folderGeneration;
        _ = RefreshEmbeddedMediaReferencesAsync(note, generation, cancellationToken);
    }

    private async Task RefreshEmbeddedMediaReferencesAsync(
        NoteItem note,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || generation != _folderGeneration
                || !ReferenceEquals(note, SelectedNote))
            {
                return;
            }

            RefreshEmbeddedMediaReferences(note);
        }
        catch (OperationCanceledException)
        {
            // A newer edit, folder switch, or shutdown superseded this refresh.
        }
    }

    private void RefreshEmbeddedMediaReferences(NoteItem note)
    {
        if (_mediaIndex is null || !CanResolveEmbeddedMediaFor(note))
        {
            return;
        }

        note.ReplaceEmbeddedMediaReferences(
            _mediaIndex.ResolveAll(note.PlainTextContent, note.SourceFilePath));
    }

    private bool CanResolveEmbeddedMediaFor(NoteItem note)
        => note.IsMarkdownFile
           && IsFolderMode
           && IsMarkdownPathInCurrentFolder(note.SourceFilePath);
}
