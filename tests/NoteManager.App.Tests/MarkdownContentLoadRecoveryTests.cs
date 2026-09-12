using Microsoft.Data.Sqlite;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class MarkdownContentLoadRecoveryTests
{
    [Theory]
    [InlineData("sharing violation", true)]
    [InlineData("access denied", false)]
    public async Task RetryLoadSelectedNote_AfterTransientReadFailure_PreservesBytesAndRestoresTheDocument(
        string failureMessage,
        bool isSharingViolation)
    {
        using var folder = new TemporaryNoteFolder();
        var readablePath = folder.WriteNote("readable.md", "# Readable");
        folder.WriteNote("protected.md", "# Original document\n\nImportant content.");
        folder.WriteNote("metadata-only.md", "# Metadata only");
        var failNextRead = false;
        using var viewModel = new MainViewModel(path =>
        {
            if (failNextRead)
            {
                // These model the transient sharing-violation and access-denied paths independently.
                failNextRead = false;
                throw isSharingViolation
                    ? new IOException(failureMessage)
                    : new UnauthorizedAccessException(failureMessage);
            }

            return File.ReadAllText(path);
        });
        await viewModel.LoadMarkdownFolderAsync(folder.Path, readablePath);
        await WaitForIndexAsync(viewModel);

        // Folder navigation may preload one item, so select an actual metadata-only note.
        var protectedNote = viewModel.NotesView.First(note => !note.IsContentLoaded);
        var originalBytes = File.ReadAllBytes(protectedNote.SourceFilePath);
        var originalContent = File.ReadAllText(protectedNote.SourceFilePath);
        // The already-loaded note keeps folder initialization out of the transient failure scenario.
        failNextRead = true;
        viewModel.SelectedNote = protectedNote;

        Assert.Same(protectedNote, viewModel.SelectedNote);
        Assert.True(protectedNote.IsContentUnavailable);
        Assert.False(protectedNote.IsContentLoaded);
        Assert.Contains("could not be read", protectedNote.ContentLoadError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(protectedNote.PlainTextContent);
        Assert.False(viewModel.CanEditSelectedNote);
        Assert.False(viewModel.CanPublishSelectedNote);
        Assert.False(viewModel.CanAssignTags);
        Assert.False(viewModel.CanImportPdfIntoNote(protectedNote));

        // Simulate a bypass of the disabled editor and prove the save guard still protects original bytes.
        protectedNote.PlainTextContent = "The Markdown file could not be read.";
        Assert.False(viewModel.TrySaveSelectedNote());
        Assert.Equal(originalBytes, File.ReadAllBytes(protectedNote.SourceFilePath));

        Assert.True(viewModel.RetryLoadSelectedNote());
        Assert.False(protectedNote.IsContentUnavailable);
        Assert.True(protectedNote.IsContentLoaded);
        Assert.Equal(originalContent, protectedNote.PlainTextContent);
        Assert.True(viewModel.CanEditSelectedNote);

        // A recovered document uses its actual loaded revision, not the discarded diagnostic text.
        const string recoveredEdit = "\n\nRecovered edit.";
        protectedNote.PlainTextContent += recoveredEdit;
        // This test already waited for folder indexing; avoid starting another unrelated index during cleanup.
        Assert.True(viewModel.TrySaveSelectedNote(updateSearchIndex: false));
        Assert.Equal(originalContent + recoveredEdit, File.ReadAllText(protectedNote.SourceFilePath));
    }

    private static async Task WaitForIndexAsync(MainViewModel viewModel)
    {
        // Saving starts a background index update, which must complete before deleting its test vault.
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
            // Test-owned folders and SQLite handles are released after each recovery scenario.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
