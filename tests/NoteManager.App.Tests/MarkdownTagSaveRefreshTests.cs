using Microsoft.Data.Sqlite;
using NoteManager.App.Models;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Database")]
public sealed class MarkdownTagSaveRefreshTests
{
    [Fact]
    public async Task RefreshMarkdownFolderAsync_WhenGitPullChangesFiles_RefreshesContentTagsAndSearchWithoutReplacingDraft()
    {
        using var folder = new TemporaryNoteFolder();
        var draftPath = folder.WriteNote("Draft.md", "draft before synchronization");
        var changedPath = folder.WriteNote(
            "Changed.md",
            "tags:\n  - old-tag\n\nold cached content");
        var deletedPath = folder.WriteNote("Deleted.md", "note removed by remote synchronization");

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(folder.Path, draftPath);
            await WaitForIndexAsync(viewModel);
            var draft = Assert.IsType<NoteItem>(viewModel.SelectedNote);
            draft.PlainTextContent = "draft typed while Git synchronization was running";

            // These writes model a remote pull changing an unselected note, adding a note, and deleting a note.
            File.WriteAllText(
                changedPath,
                "tags:\n  - remote-tag\n\nremote content search marker");
            folder.WriteNote(
                "Added.md",
                "tags:\n  - added-tag\n\nremote content search marker");
            File.Delete(deletedPath);

            await viewModel.RefreshMarkdownFolderAsync();
            await WaitForIndexAsync(viewModel);

            Assert.Same(draft, viewModel.SelectedNote);
            Assert.True(draft.IsDirty);
            Assert.Equal("draft typed while Git synchronization was running", draft.PlainTextContent);
            Assert.Contains(viewModel.NavigationItems, item => item.FilterKey == "remote-tag");
            Assert.Contains(viewModel.NavigationItems, item => item.FilterKey == "added-tag");
            Assert.DoesNotContain(viewModel.NavigationItems, item => item.FilterKey == "old-tag");
            Assert.DoesNotContain(viewModel.NotesView, note => note.SourceFilePath == deletedPath);

            viewModel.SearchText = "remote content search marker";
            await WaitForSearchAsync(viewModel);

            // Search must read the rebuilt disk index while the selected editor continues to hold its newer draft.
            Assert.Equal(
                ["Added.md", "Changed.md"],
                viewModel.NotesView.Select(note => note.FileName).OrderBy(name => name));
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RefreshMarkdownFolderAsync_WhenGitPullReplacesTheSelectedNote_ReloadsItsCachedContent()
    {
        using var folder = new TemporaryNoteFolder();
        var selectedPath = folder.WriteNote(
            "Selected.md",
            "tags:\n  - original-tag\n\nold selected content");

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(folder.Path, selectedPath);
            await WaitForIndexAsync(viewModel);
            Assert.Equal(
                "tags:\n  - original-tag\n\nold selected content",
                viewModel.SelectedNote!.PlainTextContent);

            // Git replaces this selected worktree file, so refresh must discard its old loaded buffer.
            File.WriteAllText(
                selectedPath,
                "tags:\n  - remote-tag\n\nremote selected search marker");

            await viewModel.RefreshMarkdownFolderAsync();
            await WaitForIndexAsync(viewModel);

            Assert.Equal(
                "tags:\n  - remote-tag\n\nremote selected search marker",
                viewModel.SelectedNote!.PlainTextContent);
            Assert.Contains(viewModel.NavigationItems, item => item.FilterKey == "remote-tag");
            Assert.DoesNotContain(viewModel.NavigationItems, item => item.FilterKey == "original-tag");

            viewModel.SearchText = "remote selected search marker";
            await WaitForSearchAsync(viewModel);

            // The selected note's refreshed disk content must also be searchable after the index rebuild.
            Assert.Equal(["Selected.md"], viewModel.NotesView.Select(note => note.FileName));
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [MemberData(nameof(RawMarkdownTagChanges))]
    public async Task TrySaveSelectedNote_RawMarkdownTagChangesSynchronizeTagsNavigationAndSearch(
        string initialTags,
        string savedTags,
        string selectedFilterBeforeSave,
        string expectedFilterAfterSave,
        string searchScope,
        string[] expectedTags,
        string expectedRemovedTag,
        string expectedAddedTag,
        string expectedUntaggedCount)
    {
        using var folder = new TemporaryNoteFolder();
        var editorPath = folder.WriteNote(
            "Editor.md",
            $"tags:\n{initialTags}\n\nshared search needle");
        folder.WriteNote("Untagged.md", "unrelated note");
        folder.WriteNote("Other.md", "tags:\n  - other\n\nshared search needle");

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(folder.Path, editorPath);
            await WaitForIndexAsync(viewModel);
            viewModel.SelectedNavigationItem = FindNavigationItem(viewModel, selectedFilterBeforeSave);

            // Edit the Markdown binding directly to cover the normal editor save path rather than the tag dialog.
            viewModel.SelectedNote!.PlainTextContent = $"tags:\n{savedTags}\n\nshared search needle";

            Assert.True(viewModel.TrySaveSelectedNote());
            await WaitForIndexAsync(viewModel);

            Assert.Equal(expectedTags, viewModel.SelectedNote.Tags);
            Assert.Equal(expectedFilterAfterSave, viewModel.SelectedNavigationItem?.FilterKey);
            Assert.Equal(expectedUntaggedCount, FindNavigationItem(viewModel, "__untagged__").Count);
            Assert.DoesNotContain(
                viewModel.NavigationItems,
                item => item.FilterKey.Equals(expectedRemovedTag, StringComparison.OrdinalIgnoreCase));
            if (expectedAddedTag != "__untagged__")
            {
                // Named tags are unique to the edited note; Untagged is asserted separately because it includes both untagged fixtures.
                Assert.Equal("(1)", FindNavigationItem(viewModel, expectedAddedTag).Count);
            }

            viewModel.SelectedNavigationItem = FindNavigationItem(viewModel, searchScope);
            viewModel.SearchText = "shared search needle";
            await WaitForSearchAsync(viewModel);

            // The scoped result must use the same refreshed tags that navigation displays.
            Assert.Equal(["Editor.md"], viewModel.NotesView.Select(note => note.FileName));
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    public static IEnumerable<object[]> RawMarkdownTagChanges()
    {
        // Adding retains an existing selected tag while introducing a new navigation key.
        yield return
        [
            "  - stable",
            "  - stable\n  - new",
            "stable",
            "stable",
            "stable",
            new[] { "stable", "new" },
            "old",
            "new",
            "(1)"
        ];

        // Renaming removes the selected key, so the save must fall back to All notes.
        yield return
        [
            "  - old",
            "  - new",
            "old",
            "*",
            "new",
            new[] { "new" },
            "old",
            "new",
            "(1)"
        ];

        // Removing the final tag makes the edited note visible through Untagged immediately.
        yield return
        [
            "  - old",
            string.Empty,
            "old",
            "*",
            "__untagged__",
            Array.Empty<string>(),
            "old",
            "__untagged__",
            "(2)"
        ];
    }

    private static NavigationItem FindNavigationItem(MainViewModel viewModel, string filterKey)
        => viewModel.NavigationItems.Single(item => item.FilterKey == filterKey);

    private static async Task WaitForIndexAsync(MainViewModel viewModel)
    {
        // Saving starts a background index update; wait before asserting that search and navigation agree.
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
                    $"NoteManager.MarkdownTagSaveRefreshTests.{Guid.NewGuid():N}"))
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
            // SQLite can briefly retain an index handle after cancellation, so retry cleanup deterministically.
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
}
