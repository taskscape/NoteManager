using NoteManager.App.Services;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class MarkdownMetadataParserTests
{
    [Fact]
    public void ParseTags_MultipleBlocks_MergesAndDeduplicatesInEncounterOrder()
    {
        const string markdown =
            """
            tags:
              - szablon
              - tailscale

            # Commands

            The second block may appear anywhere later in the note.

            TAGS: # another metadata block
              - szablon-komend
              - TAILSCALE
              - "szablon-poleceń"

            More note contents.

            tags:
              - release
              - szablon
            """;

        var tags = MarkdownMetadataParser.ParseTags(markdown);

        Assert.Equal(
            [
                "szablon",
                "tailscale",
                "szablon-komend",
                "szablon-poleceń",
                "release"
            ],
            tags);
    }

    [Fact]
    public void LoadFolder_MultipleTagBlocks_ExposesOneMergedTagListOnTheNote()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);

        try
        {
            File.WriteAllText(
                Path.Combine(testRoot, "multi-block.md"),
                """
                tags:
                  - first
                  - shared

                Body text between metadata blocks.

                tags:
                  - second
                  - Shared
                  - third
                """);

            var result = MarkdownFolderService.LoadFolder(testRoot);
            var note = Assert.Single(result.Notes);

            Assert.Equal(["first", "shared", "second", "third"], note.Tags);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void ParseTags_MixedCaseNames_AreAlwaysDisplayedAsLowercase()
    {
        const string markdown =
            """
            tags:
              - Product.Design
              - QUICK-START
            """;

        var tags = MarkdownMetadataParser.ParseTags(markdown);

        Assert.Equal(["product.design", "quick-start"], tags);
    }
}
