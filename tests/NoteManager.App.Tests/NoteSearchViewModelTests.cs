using Microsoft.Data.Sqlite;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

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
    public async Task SubmittedSearch_TemporarilyDisablesTheSearchInput()
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
            Assert.False(viewModel.IsSearchInputEnabled);

            await WaitForSearchToFinishAsync(viewModel);

            Assert.False(viewModel.IsSearching);
            Assert.True(viewModel.IsSearchInputEnabled);
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
}
