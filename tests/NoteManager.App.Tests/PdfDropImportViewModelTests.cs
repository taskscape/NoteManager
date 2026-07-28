using Microsoft.Data.Sqlite;
using NoteManager.App.Models;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class PdfDropImportViewModelTests
{
    [Fact]
    public async Task ImportPdfFilesAsync_ExternalCollision_UpdatesAndSavesTargetNote()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        var vaultRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "vault")).FullName;
        var noteRoot = Directory.CreateDirectory(
            Path.Combine(vaultRoot, "projects")).FullName;
        var outsideRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "outside")).FullName;
        var notePath = Path.Combine(noteRoot, "plan.md");
        var sourcePath = Path.Combine(outsideRoot, "Report.pdf");
        var copiedPath = Path.Combine(vaultRoot, "Report (1).pdf");

        File.WriteAllText(notePath, "# Plan");
        File.WriteAllText(Path.Combine(vaultRoot, "Report.pdf"), "existing");
        File.WriteAllText(sourcePath, "dropped");

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(vaultRoot);
            await WaitForIndexAsync(viewModel);
            var note = Assert.Single(viewModel.NotesView);

            await viewModel.ImportPdfFilesAsync(
                note,
                [sourcePath],
                insertionIndex: null);
            await WaitForIndexAsync(viewModel);

            Assert.True(File.Exists(copiedPath));
            Assert.Equal("dropped", File.ReadAllText(copiedPath));
            Assert.Contains("![[../Report (1).pdf]]", note.PlainTextContent);
            Assert.Contains("![[../Report (1).pdf]]", File.ReadAllText(notePath));
            Assert.Contains(
                note.EmbeddedMediaReferences,
                reference => reference.Kind == EmbeddedMediaKind.Pdf
                             && reference.ResolvedPath.Equals(
                                 copiedPath,
                                 StringComparison.OrdinalIgnoreCase));
            Assert.False(note.IsDirty);
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EditingMarkdown_RefreshesMixedMediaPreviewsInEncounterOrder()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        var vaultRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "vault")).FullName;
        var notePath = Path.Combine(vaultRoot, "plan.md");
        var imagePath = Path.Combine(vaultRoot, "diagram.png");
        var pdfPath = Path.Combine(vaultRoot, "appendix.pdf");
        File.WriteAllText(notePath, "# Plan");
        File.WriteAllText(imagePath, "image");
        File.WriteAllText(pdfPath, "pdf");

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(vaultRoot);
            await WaitForIndexAsync(viewModel);
            var note = Assert.Single(viewModel.NotesView);

            note.PlainTextContent = "![[diagram.png]]\n![[appendix.pdf]]";
            await WaitForMediaReferencesAsync(note, expectedCount: 2);

            Assert.Equal(
                [EmbeddedMediaKind.Image, EmbeddedMediaKind.Pdf],
                note.EmbeddedMediaReferences.Select(reference => reference.Kind));

            note.PlainTextContent = string.Empty;
            await WaitForMediaReferencesAsync(note, expectedCount: 0);
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
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

    private static async Task WaitForMediaReferencesAsync(
        NoteManager.App.Models.NoteItem note,
        int expectedCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (note.EmbeddedMediaReferences.Length != expectedCount
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(expectedCount, note.EmbeddedMediaReferences.Length);
    }
}
