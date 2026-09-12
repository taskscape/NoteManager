using Microsoft.Data.Sqlite;
using NoteManager.App.Models;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

/// <summary>
/// Verifies that destructive modal actions keep their captured note target across worker/UI scheduling gaps.
/// </summary>
public sealed class NoteDeletionConcurrencyTests
{
    [Fact]
    public async Task DeleteSelectedNoteAsync_WhenNavigationOccursAfterWorkerDeletes_DoesNotResurrectTheDraft()
    {
        using var folder = new TemporaryNoteFolder();
        var deletedPath = folder.WriteNote("deleted.md", "original");
        var retainedPath = folder.WriteNote("retained.md", "second note");
        using var workerDeletedFile = new ManualResetEventSlim();
        using var allowWorkerToFinish = new ManualResetEventSlim();
        using var viewModel = new MainViewModel(path =>
        {
            File.Delete(path);
            workerDeletedFile.Set();
            // Hold the worker after physical deletion so navigation runs before its UI continuation.
            allowWorkerToFinish.Wait(TimeSpan.FromSeconds(10));
        });
        await viewModel.LoadMarkdownFolderAsync(folder.Path, deletedPath);
        var deletedNote = Assert.IsType<NoteItem>(viewModel.SelectedNote);
        deletedNote.PlainTextContent = "unsaved draft";
        var retainedNote = Assert.Single(viewModel.NotesView, note =>
            note.SourceFilePath.Equals(retainedPath, StringComparison.OrdinalIgnoreCase));

        var deletion = viewModel.DeleteSelectedNoteAsync();
        await WaitForSetAsync(workerDeletedFile);
        Assert.False(File.Exists(deletedPath));

        // Navigation must not autosave the authorized-for-deletion draft back to its missing path.
        viewModel.SelectedNote = retainedNote;
        Assert.Same(retainedNote, viewModel.SelectedNote);
        Assert.False(File.Exists(deletedPath));

        allowWorkerToFinish.Set();
        Assert.True(await deletion);
        Assert.False(File.Exists(deletedPath));
        Assert.DoesNotContain(viewModel.NotesView, note => ReferenceEquals(note, deletedNote));
        // Let the deletion-triggered index writer close its SQLite handles before test cleanup.
        await WaitForIndexAsync(viewModel);
    }

    [Fact]
    public async Task DeleteSelectedNoteAsync_WhenWorkerDeleteFails_PreservesTheDirtyDraft()
    {
        using var folder = new TemporaryNoteFolder();
        var notePath = folder.WriteNote("draft.md", "original");
        // The explicit delegate selects the deletion seam over the two value-returning test seams.
        using var viewModel = new MainViewModel(new Action<string>(_ =>
            throw new IOException("Test deletion failure.")));
        await viewModel.LoadMarkdownFolderAsync(folder.Path, notePath);
        var note = Assert.IsType<NoteItem>(viewModel.SelectedNote);
        note.PlainTextContent = "unsaved draft";

        // A failed worker delete releases the state without marking or discarding the editor draft.
        Assert.False(await viewModel.DeleteSelectedNoteAsync());
        Assert.True(File.Exists(notePath));
        Assert.Same(note, viewModel.SelectedNote);
        Assert.Equal("unsaved draft", note.PlainTextContent);
        Assert.True(note.IsDirty);
        // The initial folder load also owns an asynchronous index writer.
        await WaitForIndexAsync(viewModel);
    }

    [Fact]
    public async Task DeleteNoteAsync_WhenSelectionChangesAfterConfirmation_DeletesTheConfirmedNote()
    {
        using var folder = new TemporaryNoteFolder();
        var confirmedPath = folder.WriteNote("alpha.md", "confirmed note");
        var laterSelectionPath = folder.WriteNote("beta.md", "later selection");
        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, confirmedPath);
        var target = Assert.IsType<MainViewModel.NoteOperationTarget>(
            viewModel.CreateSelectedNoteOperationTarget());
        var laterSelection = Assert.Single(viewModel.NotesView, note =>
            note.SourceFilePath.Equals(laterSelectionPath, StringComparison.OrdinalIgnoreCase));

        // This simulates changing selection while a confirmation dialog is still open.
        viewModel.SelectedNote = laterSelection;

        Assert.True(await viewModel.DeleteNoteAsync(target));
        Assert.False(File.Exists(confirmedPath));
        Assert.True(File.Exists(laterSelectionPath));
        Assert.Same(laterSelection, viewModel.SelectedNote);
        await WaitForIndexAsync(viewModel);
    }

    [Fact]
    public async Task DeleteNoteAsync_WhenConfirmedNoteIsRemovedAndVaultRefreshes_DoesNotDeleteFallbackSelection()
    {
        using var folder = new TemporaryNoteFolder();
        var confirmedPath = folder.WriteNote("alpha.md", "confirmed note");
        var fallbackPath = folder.WriteNote("beta.md", "fallback note");
        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, confirmedPath);
        var target = Assert.IsType<MainViewModel.NoteOperationTarget>(
            viewModel.CreateSelectedNoteOperationTarget());

        // An external deletion followed by a scheduled refresh makes beta the current fallback selection.
        File.Delete(confirmedPath);
        await viewModel.RefreshMarkdownFolderAsync();
        var fallbackNote = Assert.IsType<NoteItem>(viewModel.SelectedNote);
        Assert.Equal(fallbackPath, fallbackNote.SourceFilePath, ignoreCase: true);

        Assert.False(await viewModel.DeleteNoteAsync(target));
        Assert.True(File.Exists(fallbackPath));
        Assert.Contains("no longer exists", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        await WaitForIndexAsync(viewModel);
    }

    [Fact]
    public async Task DeleteNoteAsync_WhenVaultChangesAfterConfirmation_DoesNotDeleteFromEitherVault()
    {
        using var originalFolder = new TemporaryNoteFolder();
        using var laterFolder = new TemporaryNoteFolder();
        var confirmedPath = originalFolder.WriteNote("alpha.md", "confirmed note");
        var laterVaultPath = laterFolder.WriteNote("beta.md", "later vault note");
        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(originalFolder.Path, confirmedPath);
        var target = Assert.IsType<MainViewModel.NoteOperationTarget>(
            viewModel.CreateSelectedNoteOperationTarget());

        // Opening a different vault while the dialog is open must invalidate the original authorization.
        await viewModel.LoadMarkdownFolderAsync(laterFolder.Path, laterVaultPath);

        Assert.False(await viewModel.DeleteNoteAsync(target));
        Assert.True(File.Exists(confirmedPath));
        Assert.True(File.Exists(laterVaultPath));
        Assert.Contains("notes folder changed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        await WaitForIndexAsync(viewModel);
    }

    [Fact]
    public async Task ApplyTagsToNote_WhenSelectionChangesAfterDialog_UpdatesTheCapturedNote()
    {
        using var folder = new TemporaryNoteFolder();
        var confirmedPath = folder.WriteNote("alpha.md", "confirmed note");
        var laterSelectionPath = folder.WriteNote("beta.md", "later selection");
        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, confirmedPath);
        var target = Assert.IsType<MainViewModel.NoteOperationTarget>(
            viewModel.CreateSelectedNoteOperationTarget());
        var laterSelection = Assert.Single(viewModel.NotesView, note =>
            note.SourceFilePath.Equals(laterSelectionPath, StringComparison.OrdinalIgnoreCase));

        // The captured target must survive a selection change while the tags dialog awaits its result.
        viewModel.SelectedNote = laterSelection;

        Assert.True(viewModel.ApplyTagsToNote(target, ["captured-tag"]));
        Assert.Contains("captured-tag", File.ReadAllText(confirmedPath));
        Assert.DoesNotContain("captured-tag", File.ReadAllText(laterSelectionPath));
        Assert.Same(laterSelection, viewModel.SelectedNote);
        await WaitForIndexAsync(viewModel);
    }

    [Fact]
    public async Task ApplyTagsToNote_WhenConfirmedNoteIsRenamedAndVaultRefreshes_DoesNotMutateFallbackSelection()
    {
        using var folder = new TemporaryNoteFolder();
        var confirmedPath = folder.WriteNote("alpha.md", "confirmed note");
        var renamedPath = Path.Combine(folder.Path, "renamed.md");
        var fallbackPath = folder.WriteNote("beta.md", "fallback note");
        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, confirmedPath);
        var target = Assert.IsType<MainViewModel.NoteOperationTarget>(
            viewModel.CreateSelectedNoteOperationTarget());

        // A rename changes the captured path, and refresh replaces the old note object with the new vault contents.
        File.Move(confirmedPath, renamedPath);
        await viewModel.RefreshMarkdownFolderAsync();
        Assert.Equal(fallbackPath, viewModel.SelectedNote!.SourceFilePath, ignoreCase: true);

        Assert.False(viewModel.ApplyTagsToNote(target, ["captured-tag"]));
        Assert.DoesNotContain("captured-tag", File.ReadAllText(fallbackPath));
        Assert.DoesNotContain("captured-tag", File.ReadAllText(renamedPath));
        Assert.Contains("no longer exists", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        await WaitForIndexAsync(viewModel);
    }

    private static async Task WaitForSetAsync(ManualResetEventSlim gate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!gate.IsSet && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(gate.IsSet, "The controlled deletion did not remove the file in time.");
    }

    private static async Task WaitForIndexAsync(MainViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (viewModel.IsIndexing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.False(viewModel.IsIndexing);
    }

    private sealed class TemporaryNoteFolder : IDisposable
    {
        public TemporaryNoteFolder()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"NoteManager.App.Tests.{Guid.NewGuid():N}")).FullName;
        }

        public string Path { get; }

        public string WriteNote(string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            // Clear test-owned SQLite handles before deleting this isolated vault.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
