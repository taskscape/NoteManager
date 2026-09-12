using Microsoft.Data.Sqlite;
using NoteManager.App.Models;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class ExternalSaveConflictTests
{
    [Fact]
    public async Task TrySaveSelectedNote_WhenExternalContentChangesWithMatchingMetadata_PreservesBothRevisions()
    {
        using var folder = new TemporaryNoteFolder();
        var notePath = folder.WriteNote("a.md", "original");
        var originalWriteTime = File.GetLastWriteTimeUtc(notePath);
        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, notePath);
        await WaitForIndexAsync(viewModel);

        viewModel.SelectedNote!.PlainTextContent = "original + local edit";
        // Equal length and timestamp prove the guard is content-based instead of metadata-based.
        File.WriteAllText(notePath, "external");
        File.SetLastWriteTimeUtc(notePath, originalWriteTime);

        var saved = viewModel.TrySaveSelectedNote();

        Assert.False(saved);
        Assert.Equal("external", File.ReadAllText(notePath));
        Assert.Equal("original + local edit", viewModel.SelectedNote.PlainTextContent);
        Assert.True(viewModel.SelectedNote.IsDirty);
        Assert.Equal(NoteSaveConflictKind.ExternalModification, viewModel.PendingSaveConflict!.Kind);
        Assert.Equal("external", viewModel.PendingSaveConflict.ExternalContent);
    }

    [Fact]
    public async Task TrySaveSelectedNote_WhenExternalDeletionOccurs_DoesNotRecreateTheFile()
    {
        using var folder = new TemporaryNoteFolder();
        var notePath = folder.WriteNote("a.md", "original");
        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, notePath);
        await WaitForIndexAsync(viewModel);

        viewModel.SelectedNote!.PlainTextContent = "original + local edit";
        File.Delete(notePath);

        var saved = viewModel.TrySaveSelectedNote();

        Assert.False(saved);
        Assert.False(File.Exists(notePath));
        Assert.Equal("original + local edit", viewModel.SelectedNote.PlainTextContent);
        Assert.True(viewModel.SelectedNote.IsDirty);
        Assert.Equal(NoteSaveConflictKind.ExternalDeletion, viewModel.PendingSaveConflict!.Kind);
    }

    [Fact]
    public async Task TrySaveSelectedNote_WhenAnotherAppInstanceSavesFirst_PreservesTheFirstInstanceDraft()
    {
        using var folder = new TemporaryNoteFolder();
        var notePath = folder.WriteNote("a.md", "original");
        using var firstViewModel = new MainViewModel();
        using var secondViewModel = new MainViewModel();
        await firstViewModel.LoadMarkdownFolderAsync(folder.Path, notePath);
        await secondViewModel.LoadMarkdownFolderAsync(folder.Path, notePath);
        await WaitForIndexAsync(firstViewModel);
        await WaitForIndexAsync(secondViewModel);

        firstViewModel.SelectedNote!.PlainTextContent = "first instance draft";
        secondViewModel.SelectedNote!.PlainTextContent = "second instance save";
        Assert.True(secondViewModel.TrySaveSelectedNote());
        await WaitForIndexAsync(secondViewModel);

        var firstSave = firstViewModel.TrySaveSelectedNote();

        Assert.False(firstSave);
        Assert.Equal("second instance save", File.ReadAllText(notePath));
        Assert.Equal("first instance draft", firstViewModel.SelectedNote.PlainTextContent);
        Assert.True(firstViewModel.SelectedNote.IsDirty);
        Assert.Equal(NoteSaveConflictKind.ExternalModification, firstViewModel.PendingSaveConflict!.Kind);
    }

    [Fact]
    public async Task TrySaveSelectedNote_WhenGitPullChangesTheSelectedNote_PreservesTheDraftUntilResolved()
    {
        using var folder = new TemporaryNoteFolder();
        var notePath = folder.WriteNote("a.md", "original");
        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, notePath);
        await WaitForIndexAsync(viewModel);

        viewModel.SelectedNote!.PlainTextContent = "local draft";
        // This write models Git replacing the worktree file during a pull from another NoteManager process.
        File.WriteAllText(notePath, "revision from git pull");

        var saved = viewModel.TrySaveSelectedNote();

        Assert.False(saved);
        Assert.Equal("revision from git pull", File.ReadAllText(notePath));
        Assert.Equal("local draft", viewModel.SelectedNote.PlainTextContent);
        Assert.True(viewModel.HasPendingSaveConflict);
    }

    [Fact]
    public async Task TryOverwriteSelectedNoteAfterSaveConflict_WhenDiskChangesAgain_LeavesTheNewerRevisionUntouched()
    {
        using var folder = new TemporaryNoteFolder();
        var notePath = folder.WriteNote("a.md", "original");
        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, notePath);
        await WaitForIndexAsync(viewModel);

        viewModel.SelectedNote!.PlainTextContent = "local draft";
        File.WriteAllText(notePath, "first external revision");
        Assert.False(viewModel.TrySaveSelectedNote());
        File.WriteAllText(notePath, "newer external revision");

        var overwritten = viewModel.TryOverwriteSelectedNoteAfterSaveConflict();

        Assert.False(overwritten);
        Assert.Equal("newer external revision", File.ReadAllText(notePath));
        Assert.Equal("local draft", viewModel.SelectedNote.PlainTextContent);
        Assert.True(viewModel.SelectedNote.IsDirty);
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
            // Test-owned folders are removed after each case to keep filesystem state isolated.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
