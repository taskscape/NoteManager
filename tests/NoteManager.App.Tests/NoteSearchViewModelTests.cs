using Microsoft.Data.Sqlite;
using NoteManager.App.Services;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Database")]
public sealed class NoteSearchViewModelTests
{
    [Fact]
    public async Task SearchAvailability_RequiresACompletedFolderIndex()
    {
        var folderPath = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                $"NoteManager.SearchAvailabilityTests.{Guid.NewGuid():N}")).FullName;
        File.WriteAllText(
            Path.Combine(folderPath, "Invoice.md"),
            "This note contains an invoice.");
        for (var index = 0; index < 1_000; index++)
        {
            File.WriteAllText(
                Path.Combine(folderPath, $"Background {index:D4}.md"),
                $"Background indexing fixture {index}\n{new string('x', 1_024)}");
        }

        using var viewModel = new MainViewModel();
        try
        {
            Assert.False(viewModel.IsSearchAvailable);
            Assert.Equal(
                "Open a folder to search",
                viewModel.SearchPlaceholderText);

            await viewModel.LoadMarkdownFolderAsync(folderPath);
            Assert.True(viewModel.IsIndexing);
            Assert.False(viewModel.IsSearchAvailable);
            Assert.Equal(
                "Indexing in progress",
                viewModel.SearchPlaceholderText);

            await WaitForIndexAsync(viewModel);

            Assert.True(viewModel.IsSearchAvailable);
            Assert.Equal("Search notes", viewModel.SearchPlaceholderText);
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SubmittedSearch_KeepsTheSearchInputEnabled()
    {
        var folderPath = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                $"NoteManager.SearchBusyTests.{Guid.NewGuid():N}")).FullName;
        for (var index = 0; index < 2_000; index++)
        {
            File.WriteAllText(
                Path.Combine(folderPath, $"Search fixture {index:D4}.md"),
                $"search busy fixture {index}");
        }

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(folderPath);
            await WaitForIndexAsync(viewModel);

            viewModel.SearchText = "search busy fixture";
            viewModel.SubmitSearch();

            Assert.True(viewModel.IsSearching);
            Assert.True(viewModel.IsSearchInputEnabled);
            Assert.False(viewModel.IsSearchShortcutVisible);

            await WaitForSearchToFinishAsync(viewModel);

            Assert.False(viewModel.IsSearching);
            Assert.True(viewModel.IsSearchInputEnabled);
            Assert.True(viewModel.IsSearchShortcutVisible);
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SearchResultsRemainInsideTheSelectedTagScope()
    {
        var folderPath = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                $"NoteManager.SearchViewModelTests.{Guid.NewGuid():N}")).FullName;
        File.WriteAllText(
            Path.Combine(folderPath, "Alpha.md"),
            """
            tags:
              - alpha

            shared search needle
            """);
        File.WriteAllText(
            Path.Combine(folderPath, "Beta.md"),
            """
            tags:
              - beta

            shared search needle
            """);

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(folderPath);
            await WaitForIndexAsync(viewModel);
            viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(
                item => item.FilterKey == "alpha");

            viewModel.SearchText = "shared needle";
            await WaitForSearchAsync(viewModel);

            Assert.Equal(
                ["Alpha.md"],
                viewModel.NotesView.Select(note => note.Title));

            viewModel.SelectedNavigationItem = viewModel.NavigationItems.Single(
                item => item.FilterKey == "beta");

            Assert.Equal(
                ["Beta.md"],
                viewModel.NotesView.Select(note => note.Title));
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_WhenIndexReadFailsAfterASuccessfulQuery_ClearsOldHitsAndOffersRebuild(
        bool corruptDatabase)
    {
        var folderPath = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                $"NoteManager.SearchUnavailableTests.{Guid.NewGuid():N}")).FullName;
        File.WriteAllText(Path.Combine(folderPath, "Alpha.md"), "alpha marker");
        File.WriteAllText(Path.Combine(folderPath, "Beta.md"), "beta marker");

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(folderPath);
            await WaitForIndexAsync(viewModel);
            viewModel.SearchText = "alpha marker";
            await WaitForSearchAsync(viewModel);
            Assert.Equal(["Alpha.md"], viewModel.NotesView.Select(note => note.FileName));

            // Change the completed database only after pooled read connections have been released.
            SqliteConnection.ClearAllPools();
            var databasePath = NoteSearchIndexService.GetDatabasePath(folderPath);
            if (corruptDatabase)
            {
                // Corrupt bytes exercise SQLite's read-error branch after alpha has already succeeded.
                File.WriteAllText(databasePath, "this is not a SQLite database");
            }
            else
            {
                // Removing the file exercises the unavailable-database branch after alpha has already succeeded.
                File.Delete(databasePath);
            }

            viewModel.SearchText = "beta marker";
            viewModel.SubmitSearch();
            await WaitForSearchFailureAsync(viewModel);

            // A failed beta query must never present alpha's hit as a successful beta result.
            Assert.False(viewModel.IsSearchActive);
            Assert.Equal(["Alpha.md", "Beta.md"], viewModel.NotesView.Select(note => note.FileName).Order());
            Assert.False(viewModel.IsSearchAvailable);
            Assert.True(viewModel.CanRetrySearchIndex);
            Assert.Contains("beta marker", viewModel.StatusText, StringComparison.Ordinal);
            Assert.Contains("not search results", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

            // The recovery action rebuilds the database and automatically reruns the retained query.
            viewModel.RetrySearchIndexCommand.Execute(null);
            await WaitForIndexAsync(viewModel);
            await WaitForSearchAsync(viewModel);
            Assert.Equal(["Beta.md"], viewModel.NotesView.Select(note => note.FileName));
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SearchResults_KeepCaseDistinctNotesSeparateOnCaseSensitiveVolumes()
    {
        var folderPath = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                $"NoteManager.CaseDistinctSearchTests.{Guid.NewGuid():N}")).FullName;
        try
        {
            // The current Windows runner cannot create this fixture, but Linux and case-sensitive macOS volumes can.
            if (!SupportsCaseDistinctPaths(folderPath))
            {
                return;
            }

            File.WriteAllText(Path.Combine(folderPath, "A.md"), "alpha only");
            File.WriteAllText(Path.Combine(folderPath, "a.md"), "beta only");
            using var viewModel = new MainViewModel();

            await viewModel.LoadMarkdownFolderAsync(folderPath);
            await WaitForIndexAsync(viewModel);
            viewModel.SearchText = "alpha only";
            await WaitForSearchAsync(viewModel);

            // View-model hit filtering must not map A.md's hit onto the case-distinct a.md note.
            Assert.Equal(["A.md"], viewModel.NotesView.Select(note => note.FileName));

            viewModel.SearchText = "beta only";
            await WaitForSearchAsync(viewModel);
            Assert.Equal(["a.md"], viewModel.NotesView.Select(note => note.FileName));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, recursive: true);
            }
        }
    }

    private static bool SupportsCaseDistinctPaths(string folderPath)
    {
        var probeName = $"case-probe-{Guid.NewGuid():N}";
        var upperPath = Path.Combine(folderPath, probeName.ToUpperInvariant());
        var lowerPath = Path.Combine(folderPath, probeName.ToLowerInvariant());
        try
        {
            File.WriteAllText(upperPath, "upper");
            File.WriteAllText(lowerPath, "lower");
            // Two entries prove the test volume preserves the two identities independently.
            return Directory.EnumerateFiles(folderPath)
                .Count(path => Path.GetFileName(path).Equals(probeName, StringComparison.OrdinalIgnoreCase)) == 2;
        }
        finally
        {
            File.Delete(upperPath);
            if (!upperPath.Equals(lowerPath, StringComparison.Ordinal))
            {
                File.Delete(lowerPath);
            }
        }
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

    private static async Task WaitForSearchToFinishAsync(MainViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (viewModel.IsSearching && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.False(viewModel.IsSearching);
    }

    private static async Task WaitForSearchFailureAsync(MainViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (viewModel.IsSearchAvailable && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        // Availability changes only after the active query returns an unavailable search result.
        Assert.False(viewModel.IsSearchAvailable);
    }
}
