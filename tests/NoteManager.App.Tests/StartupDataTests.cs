using NoteManager.App.Services;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class StartupDataTests
{
    [Fact]
    public void DefaultViewModel_HasNoSampleNotesBeforeAFolderIsOpened()
    {
        using var viewModel = new MainViewModel();

        Assert.Empty(viewModel.NotesView);
        Assert.Null(viewModel.SelectedNote);
        Assert.False(viewModel.IsFolderMode);
    }

    [Fact]
    public void SampleData_RemainsAvailableThroughTheExplicitTestFactory()
    {
        using var viewModel = MainViewModel.CreateWithSampleDataForTesting();

        Assert.NotEmpty(viewModel.NotesView);
        Assert.NotNull(viewModel.SelectedNote);
        Assert.False(viewModel.IsFolderMode);
    }

    [Fact]
    public void LastOpenedFolder_IsPersistedAndReadOnlyWhenItStillExists()
    {
        var root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.StartupDataTests.{Guid.NewGuid():N}"));
        var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "vault"));
        var statePath = Path.Combine(root.FullName, "state", "last-folder.txt");

        try
        {
            var writer = new LastOpenedFolderService(statePath);
            Assert.True(writer.TrySave(folder.FullName));

            var reader = new LastOpenedFolderService(statePath);
            Assert.Equal(Path.GetFullPath(folder.FullName), reader.ReadExistingFolder());

            Directory.Delete(folder.FullName);
            Assert.Null(reader.ReadExistingFolder());
        }
        finally
        {
            if (Directory.Exists(root.FullName))
            {
                Directory.Delete(root.FullName, recursive: true);
            }
        }
    }
}
