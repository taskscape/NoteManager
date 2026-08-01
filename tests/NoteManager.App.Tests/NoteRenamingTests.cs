using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class NoteRenamingTests
{
    [Fact]
    public async Task CreateNewNote_WhenDefaultNameExists_UsesNumberedSuffix()
    {
        using var folder = new TemporaryNoteFolder();
        folder.WriteNote("Untitled note.md", "existing");

        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path);

        viewModel.NewNoteCommand.Execute(null);
        await WaitForSelectedFileAsync(viewModel, "Untitled note (1).md");

        Assert.True(File.Exists(Path.Combine(folder.Path, "Untitled note (1).md")));
        Assert.Equal("Untitled note (1).md", viewModel.SelectedNote!.FileName);
    }

    [Fact]
    public async Task RenameNote_MovesFileAndUpdatesSelectedNoteIdentity()
    {
        using var folder = new TemporaryNoteFolder();
        var originalPath = folder.WriteNote("Untitled note.md", "draft");

        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, originalPath);

        var renamed = viewModel.TryRenameNote(viewModel.SelectedNote!, "Project plan");

        Assert.True(renamed);
        Assert.False(File.Exists(originalPath));
        Assert.True(File.Exists(Path.Combine(folder.Path, "Project plan.md")));
        Assert.Equal("Project plan.md", viewModel.SelectedNote!.Title);
        Assert.Equal("Project plan.md", viewModel.SelectedNote.FileName);
        Assert.Equal(
            Path.Combine(folder.Path, "Project plan.md"),
            viewModel.SelectedNote.SourceFilePath);
    }

    [Fact]
    public async Task RenameNote_WhenNamesExist_UsesFirstAvailableNumberedSuffix()
    {
        using var folder = new TemporaryNoteFolder();
        var originalPath = folder.WriteNote("Untitled note.md", "draft");
        folder.WriteNote("filename.md", "existing");
        folder.WriteNote("filename (1).md", "existing numbered");

        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, originalPath);

        var renamed = viewModel.TryRenameNote(viewModel.SelectedNote!, "filename.md");

        Assert.True(renamed);
        Assert.True(File.Exists(Path.Combine(folder.Path, "filename (2).md")));
        Assert.Equal("filename (2).md", viewModel.SelectedNote!.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside.md")]
    [InlineData("not-markdown.txt")]
    public async Task RenameNote_WithInvalidName_LeavesOriginalFileInPlace(
        string requestedName)
    {
        using var folder = new TemporaryNoteFolder();
        var originalPath = folder.WriteNote("Original.md", "draft");

        using var viewModel = new MainViewModel();
        await viewModel.LoadMarkdownFolderAsync(folder.Path, originalPath);

        var renamed = viewModel.TryRenameNote(viewModel.SelectedNote!, requestedName);

        Assert.False(renamed);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("Original.md", viewModel.SelectedNote!.FileName);
    }

    private sealed class TemporaryNoteFolder : IDisposable
    {
        public TemporaryNoteFolder()
        {
            Path = Directory.CreateDirectory(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"NoteManager.RenamingTests.{Guid.NewGuid():N}"))
                .FullName;
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
                catch (Exception exception) when (
                    attempt < 4
                    && exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(150);
                }
            }
        }
    }

    private static async Task WaitForSelectedFileAsync(
        MainViewModel viewModel,
        string fileName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (viewModel.SelectedNote?.FileName != fileName
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(fileName, viewModel.SelectedNote?.FileName);
    }
}
