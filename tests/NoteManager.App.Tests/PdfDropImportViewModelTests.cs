using Microsoft.Data.Sqlite;
using NoteManager.App.ViewModels;
using System.Windows.Threading;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class PdfDropImportViewModelTests
{
    [Fact]
    public Task ImportPdfFilesAsync_ExternalCollision_UpdatesAndSavesTargetNote()
        => RunOnStaThreadAsync(async () =>
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
                var note = Assert.Single(
                    viewModel.NotesView.Cast<object>()
                        .OfType<NoteManager.App.Models.NoteItem>());

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
                    copiedPath,
                    note.PdfReferences,
                    StringComparer.OrdinalIgnoreCase);
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
        });

    private static async Task WaitForIndexAsync(MainViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (viewModel.IsIndexing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.False(viewModel.IsIndexing);
    }

    private static async Task RunOnStaThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task;
        thread.Join();
    }
}
