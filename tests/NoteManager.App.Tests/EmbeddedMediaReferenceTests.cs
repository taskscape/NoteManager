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

    /// <summary>
    /// Protects automatic attachment discovery for both supported PDF naming
    /// conventions and non-previewable related-document formats.
    /// </summary>
    [Fact]
    public void LoadFolder_CorrespondingSiblingDocuments_AreAppendedAsAttachments()
    {
        // Arrange: only exact sibling basenames should correspond to note.md.
        var root = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(root, "note.md"), "# Note");
            File.WriteAllText(Path.Combine(root, "note.docx"), "docx");
            File.WriteAllText(Path.Combine(root, "note.pdf"), "pdf");
            File.WriteAllText(Path.Combine(root, "note.md.docx"), "docx");
            File.WriteAllText(Path.Combine(root, "note.md.pdf"), "pdf");
            File.WriteAllText(Path.Combine(root, "note-backup.pdf"), "unrelated");
            File.WriteAllText(Path.Combine(root, "nested", "note.pdf"), "not a sibling");

            // Act.
            var result = MarkdownFolderService.LoadFolder(root);
            var note = Assert.Single(result.Notes);

            // Assert: PDFs remain inline-capable while other documents receive cards.
            Assert.Equal(
                ["note.docx", "note.pdf", "note.md.docx", "note.md.pdf"],
                note.EmbeddedMediaReferences.Select(reference => reference.FileName));
            Assert.Equal(
                [
                    EmbeddedMediaKind.Document,
                    EmbeddedMediaKind.Pdf,
                    EmbeddedMediaKind.Document,
                    EmbeddedMediaKind.Pdf
                ],
                note.EmbeddedMediaReferences.Select(reference => reference.Kind));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Ensures a related PDF already authored as an inline embed is rendered
    /// once rather than repeated by automatic sibling discovery.
    /// </summary>
    [Fact]
    public void LoadFolder_EmbeddedCorrespondingPdf_DoesNotDuplicateAttachment()
    {
        // Arrange.
        var root = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "note.pdf"), "pdf");
            File.WriteAllText(Path.Combine(root, "note.md.pdf"), "second pdf");
            File.WriteAllText(Path.Combine(root, "note.md"), "![[note.pdf]]");

            // Act.
            var result = MarkdownFolderService.LoadFolder(root);
            var note = Assert.Single(result.Notes);

            // Assert: the explicit embed stays first and the second related PDF follows.
            Assert.Equal(
                ["note.pdf", "note.md.pdf"],
                note.EmbeddedMediaReferences.Select(reference => reference.FileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that only PDFs gain an automatic inline preview; a related
    /// image without an explicit Markdown embed remains a document attachment.
    /// </summary>
    [Fact]
    public void LoadFolder_CorrespondingImage_IsShownAsDocumentAttachment()
    {
        // Arrange.
        var root = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "note.md"), "# Note");
            File.WriteAllText(Path.Combine(root, "note.png"), "png");

            // Act.
            var result = MarkdownFolderService.LoadFolder(root);
            var note = Assert.Single(result.Notes);

            // Assert.
            var attachment = Assert.Single(note.EmbeddedMediaReferences);
            Assert.Equal("note.png", attachment.FileName);
            Assert.Equal(EmbeddedMediaKind.Document, attachment.Kind);
            Assert.Equal("1 related document", note.AttachmentDescription);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
