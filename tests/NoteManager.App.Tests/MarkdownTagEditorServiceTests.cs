using NoteManager.App.Models;
using NoteManager.App.Services;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Unit")]
public sealed class MarkdownTagEditorServiceTests
{
    [Fact]
    public void RewriteTagBlocks_MergesEveryBlockIntoFirstAndLowercasesTags()
    {
        const string markdown =
            "# Note\r\n\r\n"
            + "tags:\r\n"
            + "  - FIRST\r\n"
            + "  - old\r\n\r\n"
            + "Body between blocks.\r\n\r\n"
            + "TAGS:\r\n"
            + "  - second\r\n"
            + "  - third\r\n\r\n"
            + "Closing text.";

        var updated = MarkdownTagEditorService.RewriteTagBlocks(
            markdown,
            ["FIRST", "release.2026", "new-tag", "first"]);

        Assert.Equal(
            "# Note\r\n\r\n"
            + "tags:\r\n"
            + "  - first\r\n"
            + "  - release.2026\r\n"
            + "  - new-tag\r\n\r\n"
            + "Body between blocks.\r\n\r\n"
            + "\r\n"
            + "Closing text.",
            updated);
        Assert.Equal(
            1,
            CountOccurrences(updated, "tags:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RewriteTagBlocks_NoBlock_AppendsAfterExactlyOneBlankLine()
    {
        const string markdown = "# Note\n\nBody.\n\n\n";

        var updated = MarkdownTagEditorService.RewriteTagBlocks(
            markdown,
            ["Product.Design", "quick-start"]);

        Assert.Equal(
            "# Note\n\nBody.\n\n"
            + "tags:\n"
            + "  - product.design\n"
            + "  - quick-start",
            updated);
    }

    [Fact]
    public void RewriteTagBlocks_NoSelectedTags_RemovesAllTagBlocks()
    {
        const string markdown =
            "tags:\n"
            + "  - first\n\n"
            + "Body.\n\n"
            + "tags:\n"
            + "  - second\n";

        var updated = MarkdownTagEditorService.RewriteTagBlocks(markdown, []);

        Assert.DoesNotContain("tags:", updated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Body.", updated);
    }

    [Fact]
    public void ParseTagInput_AcceptsMultipleNamesAndRejectsSpecialCharacters()
    {
        var result = MarkdownTagEditorService.ParseTagInput(
            "Alpha, beta-tag release.2026;naïve bad_tag @invalid alpha");

        Assert.Equal(
            ["alpha", "beta-tag", "release.2026", "naïve"],
            result.Tags);
        Assert.Equal(["bad_tag", "@invalid"], result.InvalidTags);
    }

    [Fact]
    public void CreateContext_ReturnsLowercaseRecentFiftyAndEveryRepositoryTag()
    {
        var notes = Enumerable
            .Range(0, 55)
            .Select(index => CreateNote($"Tag-{index:00}"))
            .ToArray();
        notes[0].ReplaceTags(["Tag-00", "CURRENT"]);

        var context = TagAssignmentService.CreateContext(notes, notes[0]);

        Assert.Equal(["tag-00", "current"], context.SelectedTags);
        Assert.Equal(50, context.RecentTags.Length);
        Assert.Equal("tag-00", context.RecentTags[0]);
        Assert.Equal("current", context.RecentTags[1]);
        Assert.Equal(56, context.AllTags.Length);
        Assert.Contains("tag-54", context.AllTags);
        Assert.All(
            context.AllTags,
            tag => Assert.Equal(tag.ToLowerInvariant(), tag));
    }

    private static NoteItem CreateNote(string tag)
        => new()
        {
            Title = $"{tag}.md",
            Subtitle = tag,
            FileName = $"{tag}.md",
            Size = "1 KB",
            Date = "27.07.2026",
            Notebook = "vault",
            ThumbnailKind = ThumbnailKind.Markdown,
            DocumentHeading = tag,
            DocumentSubheading = tag,
            Paragraphs = [],
            Tags = [tag]
        };

    private static int CountOccurrences(
        string source,
        string value,
        StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, comparison)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
