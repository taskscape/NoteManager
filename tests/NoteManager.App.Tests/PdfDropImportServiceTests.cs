using NoteManager.App.Services;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class PdfDropImportServiceTests
{
    [Fact]
    public void Import_ExternalPdfWithExistingName_CopiesUsingSequenceNumber()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var vaultRoot = Directory.CreateDirectory(
                Path.Combine(testRoot, "vault")).FullName;
            var outsideRoot = Directory.CreateDirectory(
                Path.Combine(testRoot, "outside")).FullName;
            File.WriteAllText(Path.Combine(vaultRoot, "file.pdf"), "existing");
            var sourcePath = Path.Combine(outsideRoot, "file.pdf");
            File.WriteAllText(sourcePath, "dropped");

            var firstImport = PdfDropImportService.Import(sourcePath, vaultRoot);
            var secondImport = PdfDropImportService.Import(sourcePath, vaultRoot);

            Assert.True(firstImport.WasCopied);
            Assert.Equal(
                Path.Combine(vaultRoot, "file (1).pdf"),
                firstImport.DestinationPath);
            Assert.Equal("![[file (1).pdf]]", firstImport.MarkdownEmbed);
            Assert.Equal("dropped", File.ReadAllText(firstImport.DestinationPath));
            Assert.Equal(
                Path.Combine(vaultRoot, "file (2).pdf"),
                secondImport.DestinationPath);
            Assert.Equal("![[file (2).pdf]]", secondImport.MarkdownEmbed);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void Import_PdfAlreadyInsideVault_UsesVaultRelativeEmbedWithoutCopying()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var vaultRoot = Directory.CreateDirectory(
                Path.Combine(testRoot, "vault")).FullName;
            var documentsRoot = Directory.CreateDirectory(
                Path.Combine(vaultRoot, "Documents")).FullName;
            var sourcePath = Path.Combine(documentsRoot, "Report.pdf");
            File.WriteAllText(sourcePath, "report");

            var imported = PdfDropImportService.Import(sourcePath, vaultRoot);

            Assert.False(imported.WasCopied);
            Assert.Equal(sourcePath, imported.DestinationPath);
            Assert.Equal("![[Documents/Report.pdf]]", imported.MarkdownEmbed);
            Assert.Single(Directory.EnumerateFiles(
                vaultRoot,
                "*.pdf",
                SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void Import_ExternalPdfForNestedNote_UsesUnambiguousNoteRelativeEmbed()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var vaultRoot = Directory.CreateDirectory(
                Path.Combine(testRoot, "vault")).FullName;
            var noteRoot = Directory.CreateDirectory(
                Path.Combine(vaultRoot, "projects")).FullName;
            var outsideRoot = Directory.CreateDirectory(
                Path.Combine(testRoot, "outside")).FullName;
            var notePath = Path.Combine(noteRoot, "plan.md");
            File.WriteAllText(notePath, "# Plan");
            File.WriteAllText(Path.Combine(noteRoot, "file.pdf"), "shadow");
            var sourcePath = Path.Combine(outsideRoot, "file.pdf");
            File.WriteAllText(sourcePath, "dropped");

            var imported = PdfDropImportService.Import(
                sourcePath,
                vaultRoot,
                notePath);

            Assert.True(imported.WasCopied);
            Assert.Equal(
                Path.Combine(vaultRoot, "file.pdf"),
                imported.DestinationPath);
            Assert.Equal("![[../file.pdf]]", imported.MarkdownEmbed);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void InsertMarkdownEmbeds_AtCaret_CreatesASeparateMarkdownBlock()
    {
        var markdown = "BeforeAfter";

        var updated = PdfDropImportService.InsertMarkdownEmbeds(
            markdown,
            ["![[file.pdf]]", "![[file (1).pdf]]"],
            insertionIndex: 6);

        Assert.Equal(
            $"Before{Environment.NewLine}{Environment.NewLine}"
            + $"![[file.pdf]]{Environment.NewLine}![[file (1).pdf]]"
            + $"{Environment.NewLine}{Environment.NewLine}After",
            updated);
    }

    [Fact]
    public void InsertMarkdownEmbeds_PreservesLfLineEndings()
    {
        var updated = PdfDropImportService.InsertMarkdownEmbeds(
            "First\nSecond",
            ["![[file.pdf]]"],
            insertionIndex: 5);

        Assert.Equal("First\n\n![[file.pdf]]\nSecond", updated);
        Assert.DoesNotContain('\r', updated);
    }

    private static string CreateTestRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
