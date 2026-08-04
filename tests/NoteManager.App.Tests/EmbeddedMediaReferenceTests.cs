using NoteManager.App.Models;
using NoteManager.App.Services;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Unit")]
public sealed class EmbeddedMediaReferenceTests
{
    [Fact]
    public void ParseInlineEmbeddedMediaEmbeds_SupportsSupportedImageFormatsAndPdf()
    {
        const string markdown =
            "![[first.PNG]]\n![[second.jpg|A photo]]\n![[third.JPEG#details]]\n![[fourth.bmp]]\n![[fifth.pdf]]";

        var references = MarkdownMetadataParser.ParseInlineEmbeddedMediaEmbeds(markdown);

        Assert.Equal(
            ["first.PNG", "second.jpg", "third.JPEG", "fourth.bmp", "fifth.pdf"],
            references);
    }

    [Fact]
    public void LoadFolder_MixedMediaEmbeds_AreResolvedInMarkdownOrder()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            File.WriteAllText(Path.Combine(root, "assets", "first.png"), "png");
            File.WriteAllText(Path.Combine(root, "assets", "second.jpg"), "jpg");
            File.WriteAllText(Path.Combine(root, "third.bmp"), "bmp");
            File.WriteAllText(Path.Combine(root, "appendix.pdf"), "pdf");
            File.WriteAllText(
                Path.Combine(root, "note.md"),
                "![[assets/first.png]]\n![[appendix.pdf]]\n![[assets/second.jpg]]\n![[third.bmp]]");

            var result = MarkdownFolderService.LoadFolder(root);
            var note = Assert.Single(result.Notes);

            Assert.Equal(
                [
                    EmbeddedMediaKind.Image,
                    EmbeddedMediaKind.Pdf,
                    EmbeddedMediaKind.Image,
                    EmbeddedMediaKind.Image
                ],
                note.EmbeddedMediaReferences.Select(reference => reference.Kind));
            Assert.Equal(
                ["first.png", "appendix.pdf", "second.jpg", "third.bmp"],
                note.EmbeddedMediaReferences.Select(reference => reference.FileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
