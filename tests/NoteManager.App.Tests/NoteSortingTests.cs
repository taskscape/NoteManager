using System.Text.Json;
using NoteManager.App.Models;
using NoteManager.App.Services;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class NoteSortingTests
{
    [Fact]
    public async Task FolderNotes_DefaultToRecentlyUpdatedFirst()
    {
        using var folder = new TemporaryNoteFolder();
        folder.WriteNote("Older.md", "small", createdDaysAgo: 1, updatedDaysAgo: 3);
        folder.WriteNote("Newer.md", "newest", createdDaysAgo: 2, updatedDaysAgo: 1);

        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path);

        Assert.Equal(NoteSortType.Updated, viewModel.SelectedSortType);
        Assert.Equal(
            ["Newer.md", "Older.md"],
            viewModel.NotesView.Select(note => note.Title));
        using var settings = JsonDocument.Parse(
            File.ReadAllText(
                NoteSortPreferenceService.GetSettingsPath(folder.Path)));
        Assert.Equal(
            "Updated",
            settings.RootElement.GetProperty("sortType").GetString());
    }

    [Fact]
    public async Task SortTypes_UseFileMetadataAndPersistTheSelection()
    {
        using var folder = new TemporaryNoteFolder();
        folder.WriteNote(
            "Bravo.md",
            "largest note body",
            createdDaysAgo: 1,
            updatedDaysAgo: 3);
        folder.WriteNote(
            "Alpha.md",
            "x",
            createdDaysAgo: 4,
            updatedDaysAgo: 1);

        using (var viewModel = new MainViewModel())
        {
            await viewModel.LoadMarkdownFolderAsync(folder.Path);

            viewModel.SetSortType(NoteSortType.Title);
            Assert.Equal(
                ["Alpha.md", "Bravo.md"],
                viewModel.NotesView.Select(note => note.Title));

            viewModel.SetSortType(NoteSortType.Created);
            Assert.Equal(
                ["Bravo.md", "Alpha.md"],
                viewModel.NotesView.Select(note => note.Title));

            viewModel.SetSortType(NoteSortType.Updated);
            Assert.Equal(
                ["Alpha.md", "Bravo.md"],
                viewModel.NotesView.Select(note => note.Title));

            viewModel.SetSortType(NoteSortType.Size);
            Assert.Equal(
                ["Bravo.md", "Alpha.md"],
                viewModel.NotesView.Select(note => note.Title));
        }

        var settingsPath = NoteSortPreferenceService.GetSettingsPath(folder.Path);
        Assert.True(File.Exists(settingsPath));
        using (var settings = JsonDocument.Parse(File.ReadAllText(settingsPath)))
        {
            Assert.Equal(
                "Size",
                settings.RootElement.GetProperty("sortType").GetString());
        }

        using var reloadedViewModel = new MainViewModel();
        await reloadedViewModel.LoadMarkdownFolderAsync(folder.Path);

        Assert.Equal(NoteSortType.Size, reloadedViewModel.SelectedSortType);
        Assert.True(reloadedViewModel.IsSortBySize);
        Assert.Equal(
            ["Bravo.md", "Alpha.md"],
            reloadedViewModel.NotesView.Select(note => note.Title));
    }

    [Fact]
    public async Task AcceptedSearch_ClearsSortSelectionAndClearingSearchRestoresIt()
    {
        using var folder = new TemporaryNoteFolder();
        folder.WriteNote(
            "Zulu.md",
            "alpha beta",
            createdDaysAgo: 2,
            updatedDaysAgo: 1);
        folder.WriteNote(
            "Alpha.md",
            "alpha beta",
            createdDaysAgo: 4,
            updatedDaysAgo: 3);
        folder.WriteNote(
            "Middle.md",
            "unrelated",
            createdDaysAgo: 1,
            updatedDaysAgo: 0);

        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path);
        await WaitForIndexAsync(viewModel);
        viewModel.SetSortType(NoteSortType.Title);

        viewModel.SearchText = "alpha beta";
        await WaitForSearchAsync(viewModel);

        Assert.True(viewModel.IsSearchActive);
        Assert.False(viewModel.CanSortNotes);
        Assert.False(viewModel.IsSortByTitle);
        Assert.False(viewModel.IsSortByCreated);
        Assert.False(viewModel.IsSortByUpdated);
        Assert.False(viewModel.IsSortBySize);
        Assert.Equal(
            ["Zulu.md", "Alpha.md"],
            viewModel.NotesView.Select(note => note.Title));

        viewModel.SearchText = "\"unfinished";

        Assert.True(viewModel.IsSearchActive);
        Assert.False(viewModel.CanSortNotes);
        Assert.Contains(
            "Incomplete quoted phrase",
            viewModel.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["Zulu.md", "Alpha.md"],
            viewModel.NotesView.Select(note => note.Title));

        viewModel.SearchText = string.Empty;

        Assert.False(viewModel.IsSearchActive);
        Assert.True(viewModel.CanSortNotes);
        Assert.True(viewModel.IsSortByTitle);
        Assert.Equal(
            ["Alpha.md", "Middle.md", "Zulu.md"],
            viewModel.NotesView.Select(note => note.Title));
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

    private static async Task WaitForSearchAsync(MainViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!viewModel.IsSearchActive && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(viewModel.IsSearchActive);
    }

    private sealed class TemporaryNoteFolder : IDisposable
    {
        public TemporaryNoteFolder()
        {
            Path = Directory.CreateDirectory(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"NoteManager.SortingTests.{Guid.NewGuid():N}")).FullName;
        }

        public string Path { get; }

        public void WriteNote(
            string fileName,
            string content,
            int createdDaysAgo,
            int updatedDaysAgo)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content);
            var now = DateTime.UtcNow;
            File.SetCreationTimeUtc(path, now.AddDays(-createdDaysAgo));
            File.SetLastWriteTimeUtc(path, now.AddDays(-updatedDaysAgo));
        }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(Path))
                    {
                        Directory.Delete(Path, recursive: true);
                    }

                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(150);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(150);
                }
            }
        }
    }
}
