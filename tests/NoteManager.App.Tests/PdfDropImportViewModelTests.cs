using Microsoft.Data.Sqlite;
using NoteManager.App.Models;
using NoteManager.App.ViewModels;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Database")]
public sealed class PdfDropImportViewModelTests
{
    [Fact]
    public async Task ImportPdfFilesAsync_SaveDuringPausedImport_EmbedsAndPreservesCopy()
    {
        var testRoot = CreateTestRoot();
        var vaultRoot = Directory.CreateDirectory(Path.Combine(testRoot, "vault")).FullName;
        var outsideRoot = Directory.CreateDirectory(Path.Combine(testRoot, "outside")).FullName;
        var notePath = Path.Combine(vaultRoot, "plan.md");
        var sourcePath = Path.Combine(outsideRoot, "Report.pdf");
        var copiedPath = Path.Combine(vaultRoot, "Report.pdf");
        File.WriteAllText(notePath, "# Original plan");
        File.WriteAllText(sourcePath, "dropped PDF");

        using var postCopyReached = new ManualResetEventSlim();
        using var continueImport = new ManualResetEventSlim();
        var viewModel = new MainViewModel(() =>
        {
            // Pause after the owned file exists to reproduce an index restart before its continuation.
            postCopyReached.Set();
            continueImport.Wait(TimeSpan.FromSeconds(10));
        });
        try
        {
            await viewModel.LoadMarkdownFolderAsync(vaultRoot);
            await WaitForIndexAsync(viewModel);
            var note = Assert.Single(viewModel.NotesView);

            var importTask = viewModel.ImportPdfFilesAsync(note, [sourcePath], insertionIndex: null);
            Assert.True(postCopyReached.Wait(TimeSpan.FromSeconds(10)));

            // An ordinary save restarts indexing but must not make this same-vault import obsolete.
            note.PlainTextContent = "# Saved before import continuation";
            Assert.True(viewModel.TrySaveSelectedNote());
            continueImport.Set();
            await importTask;
            // Wait for the import save's index run before releasing this vault's SQLite files.
            await WaitForIndexAsync(viewModel);

            Assert.True(File.Exists(copiedPath));
            Assert.Contains("![[Report.pdf]]", note.PlainTextContent);
            Assert.Contains("![[Report.pdf]]", File.ReadAllText(notePath));
        }
        finally
        {
            // Always release the worker so a failed assertion cannot leave a background task blocked.
            continueImport.Set();
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task ImportPdfFilesAsync_RenameDuringPausedImport_EmbedsInRenamedNote()
    {
        var testRoot = CreateTestRoot();
        var vaultRoot = Directory.CreateDirectory(Path.Combine(testRoot, "vault")).FullName;
        var outsideRoot = Directory.CreateDirectory(Path.Combine(testRoot, "outside")).FullName;
        var sourcePath = Path.Combine(outsideRoot, "Report.pdf");
        var copiedPath = Path.Combine(vaultRoot, "Report.pdf");
        File.WriteAllText(Path.Combine(vaultRoot, "plan.md"), "# Original plan");
        File.WriteAllText(sourcePath, "dropped PDF");

        using var postCopyReached = new ManualResetEventSlim();
        using var continueImport = new ManualResetEventSlim();
        var viewModel = new MainViewModel(() =>
        {
            // Hold the continuation so rename starts its normal background index while the copy is in flight.
            postCopyReached.Set();
            continueImport.Wait(TimeSpan.FromSeconds(10));
        });
        try
        {
            await viewModel.LoadMarkdownFolderAsync(vaultRoot);
            await WaitForIndexAsync(viewModel);
            var note = Assert.Single(viewModel.NotesView);

            var importTask = viewModel.ImportPdfFilesAsync(note, [sourcePath], insertionIndex: null);
            Assert.True(postCopyReached.Wait(TimeSpan.FromSeconds(10)));

            // Renaming keeps the same note object and vault, so it must not discard the import.
            Assert.True(viewModel.TryRenameNote(note, "Renamed plan"));
            continueImport.Set();
            await importTask;
            // Wait for the import save's index run before releasing this vault's SQLite files.
            await WaitForIndexAsync(viewModel);

            var renamedPath = Path.Combine(vaultRoot, "Renamed plan.md");
            Assert.True(File.Exists(copiedPath));
            Assert.True(File.Exists(renamedPath));
            Assert.Contains("![[Report.pdf]]", note.PlainTextContent);
            Assert.Contains("![[Report.pdf]]", File.ReadAllText(renamedPath));
        }
        finally
        {
            // Always release the worker so a failed assertion cannot leave a background task blocked.
            continueImport.Set();
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task ImportPdfFilesAsync_VaultSwitchDuringPausedImport_RollsBackOwnedCopy()
    {
        var testRoot = CreateTestRoot();
        var firstVaultRoot = Directory.CreateDirectory(Path.Combine(testRoot, "first-vault")).FullName;
        var secondVaultRoot = Directory.CreateDirectory(Path.Combine(testRoot, "second-vault")).FullName;
        var outsideRoot = Directory.CreateDirectory(Path.Combine(testRoot, "outside")).FullName;
        var firstNotePath = Path.Combine(firstVaultRoot, "plan.md");
        var sourcePath = Path.Combine(outsideRoot, "Report.pdf");
        var copiedPath = Path.Combine(firstVaultRoot, "Report.pdf");
        File.WriteAllText(firstNotePath, "# First vault");
        File.WriteAllText(Path.Combine(secondVaultRoot, "other.md"), "# Second vault");
        File.WriteAllText(sourcePath, "dropped PDF");

        using var postCopyReached = new ManualResetEventSlim();
        using var continueImport = new ManualResetEventSlim();
        var viewModel = new MainViewModel(() =>
        {
            // Pause after copying so the completed vault switch is the sole cancellation cause.
            postCopyReached.Set();
            continueImport.Wait(TimeSpan.FromSeconds(10));
        });
        try
        {
            await viewModel.LoadMarkdownFolderAsync(firstVaultRoot);
            await WaitForIndexAsync(viewModel);
            var firstVaultNote = Assert.Single(viewModel.NotesView);

            var importTask = viewModel.ImportPdfFilesAsync(firstVaultNote, [sourcePath], insertionIndex: null);
            Assert.True(postCopyReached.Wait(TimeSpan.FromSeconds(10)));

            // A completed vault transition invalidates the original import and requires cleanup.
            await viewModel.LoadMarkdownFolderAsync(secondVaultRoot);
            await WaitForIndexAsync(viewModel);
            continueImport.Set();
            await importTask;

            Assert.False(File.Exists(copiedPath));
            Assert.DoesNotContain("![[Report.pdf]]", File.ReadAllText(firstNotePath));
            Assert.Contains("PDF import cancelled because the notes folder", viewModel.StatusText);
        }
        finally
        {
            // Always release the worker so a failed assertion cannot leave a background task blocked.
            continueImport.Set();
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteTestRoot(testRoot);
        }
    }

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
    public async Task ImportPdfDocumentsAsync_ExternalPdf_CopiesWithoutChangingTheSelectedNote()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        var vaultRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "vault")).FullName;
        var outsideRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "outside")).FullName;
        var notePath = Path.Combine(vaultRoot, "plan.md");
        var sourcePath = Path.Combine(outsideRoot, "Report.pdf");

        File.WriteAllText(notePath, "# Existing plan");
        File.WriteAllText(sourcePath, "dropped PDF");

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(vaultRoot);
            await WaitForIndexAsync(viewModel);
            var selectedNote = Assert.Single(viewModel.NotesView);

            var imported = await viewModel.ImportPdfDocumentsAsync([sourcePath]);

            // Document imports leave the current Markdown untouched for the converter to create a separate counterpart.
            var importedPdf = Assert.Single(imported);
            Assert.True(importedPdf.WasCopied);
            Assert.Equal(Path.Combine(vaultRoot, "Report.pdf"), importedPdf.DestinationPath);
            Assert.Equal("# Existing plan", selectedNote.PlainTextContent);
            Assert.Equal("# Existing plan", File.ReadAllText(notePath));
            Assert.Contains("Imported 1 PDF document", viewModel.StatusText);
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
    public async Task RefreshMarkdownFolderAsync_AddsConvertedNotesAndPreservesTheUnsavedSelection()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.App.Tests.{Guid.NewGuid():N}");
        var vaultRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "vault")).FullName;
        var notePath = Path.Combine(vaultRoot, "plan.md");
        File.WriteAllText(notePath, "# Saved plan");

        using var viewModel = new MainViewModel();
        try
        {
            await viewModel.LoadMarkdownFolderAsync(vaultRoot);
            await WaitForIndexAsync(viewModel);
            var selectedNote = Assert.Single(viewModel.NotesView);
            selectedNote.PlainTextContent = "# Unsaved draft";
            File.WriteAllText(
                Path.Combine(vaultRoot, "converted.md"),
                "background conversion search marker");

            await viewModel.RefreshMarkdownFolderAsync();
            await WaitForIndexAsync(viewModel);

            Assert.Same(selectedNote, viewModel.SelectedNote);
            Assert.True(selectedNote.IsDirty);
            Assert.Equal("# Unsaved draft", selectedNote.PlainTextContent);
            Assert.Equal("# Saved plan", File.ReadAllText(notePath));
            Assert.Contains(viewModel.NotesView, note => note.FileName == "converted.md");

            viewModel.SearchText = "background conversion search marker";
            await WaitForSearchAsync(viewModel);
            Assert.Contains(viewModel.NotesView, note => note.FileName == "converted.md");
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

    private static async Task WaitForSearchAsync(MainViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!viewModel.IsSearchActive && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(viewModel.IsSearchActive);
    }

    // Shared cleanup keeps each race test isolated even when it creates more than one vault.
    private static string CreateTestRoot()
        => Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(),
                $"NoteManager.App.Tests.{Guid.NewGuid():N}"))
            .FullName;

    // SQLite pools are cleared by callers before removal because index tasks may have touched the vault.
    private static void DeleteTestRoot(string testRoot)
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
