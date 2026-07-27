using Microsoft.Data.Sqlite;
using NoteManager.App.Services;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class NoteSearchIndexServiceTests
{
    [Fact]
    public void UpdateIndex_WhenCancelledAfterCommittedBatch_ReturnsNormally()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "NoteManager.App.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            for (var index = 0; index < 201; index++)
            {
                File.WriteAllText(
                    Path.Combine(testRoot, $"note-{index:D3}.md"),
                    $"# Note {index}{Environment.NewLine}{Environment.NewLine}searchable content");
            }

            using var cancellation = new CancellationTokenSource();
            var progress = new CancelAfterFirstBatchProgress(cancellation);

            var result = NoteSearchIndexService.UpdateIndex(
                testRoot,
                progress,
                cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(201, result.TotalFiles);
            Assert.Equal(200, result.UpdatedFiles);

            var searchResult = NoteSearchIndexService.Search(
                testRoot,
                "searchable",
                maxResults: 1_000,
                CancellationToken.None);
            Assert.True(searchResult.IsAvailable);
            Assert.Equal(200, searchResult.Paths.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private sealed class CancelAfterFirstBatchProgress(
        CancellationTokenSource cancellation) : IProgress<NoteSearchIndexProgress>
    {
        public void Report(NoteSearchIndexProgress value)
        {
            if (value.ProcessedFiles >= 200)
            {
                cancellation.Cancel();
            }
        }
    }
}
