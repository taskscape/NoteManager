using Microsoft.Data.Sqlite;
using NoteManager.App.Models;
using NoteManager.App.Services;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

/// <summary>
/// Covers the guarded hand-off between vaults so a late draft cannot become
/// detached from the folder and collection that own it.
/// </summary>
public sealed class FolderLoadTransitionTests
{
    [Fact]
    public async Task LoadMarkdownFolderAsync_WhenLateDraftSaveFails_KeepsTheOldVaultAndDraftUsable()
    {
        using var firstFolder = new TemporaryNoteFolder();
        using var secondFolder = new TemporaryNoteFolder();
        var firstNotePath = firstFolder.WriteNote("first.md", "initial draft");
        secondFolder.WriteNote("second.md", "second vault note");
        using var loadStarted = new ManualResetEventSlim();
        using var allowLoadToFinish = new ManualResetEventSlim();
        using var viewModel = new MainViewModel(path =>
        {
            if (path.Equals(secondFolder.Path, StringComparison.OrdinalIgnoreCase))
            {
                loadStarted.Set();
                allowLoadToFinish.Wait(TimeSpan.FromSeconds(10));
            }

            return MarkdownFolderService.LoadFolder(path);
        });
        await viewModel.LoadMarkdownFolderAsync(firstFolder.Path, firstNotePath);
        var originalNote = Assert.IsType<NoteItem>(viewModel.SelectedNote);

        // Arrange a slow read so the draft mutation occurs after the initial save but before the commit save.
        var switchTask = viewModel.LoadMarkdownFolderAsync(secondFolder.Path);
        await WaitForSetAsync(loadStarted);
        Assert.False(viewModel.CanEditSelectedNote);
        originalNote.PlainTextContent = "late draft";

        // This share mode permits reading but rejects the atomic replacement used by the final save.
        using (var replacementLock = new FileStream(
                   firstNotePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            allowLoadToFinish.Set();
            await switchTask;

            Assert.Equal(Path.GetFullPath(firstFolder.Path), viewModel.CurrentFolderPath);
            Assert.Same(originalNote, viewModel.SelectedNote);
            Assert.Contains(originalNote, viewModel.NotesView);
            Assert.Equal("late draft", originalNote.PlainTextContent);
            Assert.True(originalNote.IsDirty);
        }

        // Releasing the transient lock must leave the retained draft saveable in its original vault.
        Assert.True(viewModel.CanEditSelectedNote);
        Assert.True(viewModel.TrySaveSelectedNote());
        Assert.Equal("late draft", File.ReadAllText(firstNotePath));
        // Wait for the save-triggered index writer before the test folder is disposed.
        await WaitForIndexAsync(viewModel);
    }

    [Fact]
    public async Task LoadMarkdownFolderAsync_WhenRequestsCompleteInReverseOrder_AppliesOnlyTheNewestFolder()
    {
        using var firstFolder = new TemporaryNoteFolder();
        using var secondFolder = new TemporaryNoteFolder();
        using var thirdFolder = new TemporaryNoteFolder();
        firstFolder.WriteNote("first.md", "first vault");
        secondFolder.WriteNote("second.md", "second vault");
        var thirdNotePath = thirdFolder.WriteNote("third.md", "third vault");
        using var secondLoadStarted = new ManualResetEventSlim();
        using var thirdLoadStarted = new ManualResetEventSlim();
        using var allowSecondLoadToFinish = new ManualResetEventSlim();
        using var allowThirdLoadToFinish = new ManualResetEventSlim();
        using var viewModel = new MainViewModel(path =>
        {
            if (path.Equals(secondFolder.Path, StringComparison.OrdinalIgnoreCase))
            {
                secondLoadStarted.Set();
                allowSecondLoadToFinish.Wait(TimeSpan.FromSeconds(10));
            }
            else if (path.Equals(thirdFolder.Path, StringComparison.OrdinalIgnoreCase))
            {
                thirdLoadStarted.Set();
                allowThirdLoadToFinish.Wait(TimeSpan.FromSeconds(10));
            }

            return MarkdownFolderService.LoadFolder(path);
        });
        await viewModel.LoadMarkdownFolderAsync(firstFolder.Path);

        // Complete the newest request first, then release the obsolete request to prove it cannot apply late.
        var secondLoad = viewModel.LoadMarkdownFolderAsync(secondFolder.Path);
        await WaitForSetAsync(secondLoadStarted);
        var thirdLoad = viewModel.LoadMarkdownFolderAsync(thirdFolder.Path, thirdNotePath);
        await WaitForSetAsync(thirdLoadStarted);
        allowThirdLoadToFinish.Set();
        await thirdLoad;
        allowSecondLoadToFinish.Set();
        await secondLoad;

        Assert.Equal(Path.GetFullPath(thirdFolder.Path), viewModel.CurrentFolderPath);
        Assert.Equal("third.md", viewModel.SelectedNote!.FileName);
        Assert.Single(viewModel.NotesView);
        Assert.Equal("third.md", viewModel.NotesView[0].FileName);
        Assert.False(viewModel.IsLoadingFolder);
        // The newest folder owns the active index, so let its database handles close before cleanup.
        await WaitForIndexAsync(viewModel);
    }

    [Fact]
    public async Task RefreshMarkdownFolderAsync_WhenUserChangesNoteFilterAndDraftDuringRead_KeepsTheLiveState()
    {
        using var folder = new TemporaryNoteFolder();
        var aPath = folder.WriteNote("a.md", "tags:\n  - alpha\n\nA saved content");
        var bPath = folder.WriteNote("b.md", "tags:\n  - beta\n\nB saved content");
        using var refreshStarted = new ManualResetEventSlim();
        using var allowRefreshToFinish = new ManualResetEventSlim();
        var pauseRefresh = 0;
        using var viewModel = new MainViewModel(path =>
        {
            // Pause only the refresh read so the initial folder load establishes the starting selection.
            if (Volatile.Read(ref pauseRefresh) != 0)
            {
                refreshStarted.Set();
                allowRefreshToFinish.Wait(TimeSpan.FromSeconds(10));
            }

            return MarkdownFolderService.LoadFolder(path);
        });
        await viewModel.LoadMarkdownFolderAsync(folder.Path, bPath);

        // Start from B, then make the user changes while the refresh worker is paused.
        // Publish the pause request to the Task.Run loader thread before starting the refresh.
        Volatile.Write(ref pauseRefresh, 1);
        var refreshTask = viewModel.RefreshMarkdownFolderAsync();
        await WaitForSetAsync(refreshStarted);
        var userSelectedNote = viewModel.NotesView.Single(note => note.SourceFilePath == aPath);
        viewModel.SelectedNote = userSelectedNote;
        viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(item => item.FilterKey == "alpha");
        userSelectedNote.PlainTextContent = "A draft typed during refresh";

        allowRefreshToFinish.Set();
        await refreshTask;
        await WaitForIndexAsync(viewModel);

        Assert.Same(userSelectedNote, viewModel.SelectedNote);
        Assert.Equal("a.md", viewModel.SelectedNote.FileName);
        Assert.Equal("alpha", viewModel.SelectedNavigationItem?.FilterKey);
        Assert.Equal("A draft typed during refresh", viewModel.SelectedNote.PlainTextContent);
        Assert.True(viewModel.SelectedNote.IsDirty);
    }

    private static async Task WaitForSetAsync(ManualResetEventSlim gate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!gate.IsSet && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(gate.IsSet, "The controlled folder load did not reach its synchronization point.");
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
            // Clear test-owned SQLite handles before removing the isolated vault directory.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
