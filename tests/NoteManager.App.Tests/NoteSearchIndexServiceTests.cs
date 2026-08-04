using Microsoft.Data.Sqlite;
using NoteManager.App.Services;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Database")]
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
            Assert.Equal(200, searchResult.Hits.Count);
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

    [Fact]
    public void Search_WhenAlreadyCancelled_ReturnsCanceledResultWithoutThrowing()
    {
        using var folder = new SearchTestFolder();
        folder.WriteNote(
            "Cancelled.md",
            "search cancellation fixture",
            modifiedDaysAgo: 0);
        folder.UpdateIndex();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = NoteSearchIndexService.Search(
            folder.Path,
            "cancellation",
            maxResults: 100,
            cancellation.Token);

        Assert.True(result.IsCanceled);
        Assert.True(result.IsAvailable);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Search_StrictMode_RequiresEveryTermAndOrdersByModificationTime()
    {
        using var folder = new SearchTestFolder();
        folder.WriteNote(
            "Newest.md",
            "project planning alpha",
            modifiedDaysAgo: 1);
        folder.WriteNote(
            "Older.md",
            "project plan beta",
            modifiedDaysAgo: 3);
        folder.WriteNote(
            "Unrelated.md",
            "cooking notes",
            modifiedDaysAgo: 0);
        folder.UpdateIndex();

        var result = NoteSearchIndexService.Search(
            folder.Path,
            "project plan",
            maxResults: 100,
            CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal(NoteSearchMode.Strict, result.Mode);
        Assert.Equal(
            ["Newest.md", "Older.md"],
            result.Hits.Select(hit => hit.Name));
    }

    [Fact]
    public void Search_QuotedPhrasesAndLiteralSeparators_ArePreserved()
    {
        using var folder = new SearchTestFolder();
        folder.WriteNote(
            Path.Combine("docs", "search.md"),
            """
            # Search

            The quarterly project plan is stored at docs/search.md.
            """,
            modifiedDaysAgo: 1);
        folder.WriteNote(
            "Separated.md",
            "project status followed later by a plan",
            modifiedDaysAgo: 0);
        folder.UpdateIndex();

        var phraseResult = NoteSearchIndexService.Search(
            folder.Path,
            "\"project plan\"",
            maxResults: 100,
            CancellationToken.None);
        var slashResult = NoteSearchIndexService.Search(
            folder.Path,
            "body:docs/search.md",
            maxResults: 100,
            CancellationToken.None);
        var backslashResult = NoteSearchIndexService.Search(
            folder.Path,
            @"path:docs\search.md",
            maxResults: 100,
            CancellationToken.None);

        Assert.Equal(["search.md"], phraseResult.Hits.Select(hit => hit.Name));
        Assert.Equal(["search.md"], slashResult.Hits.Select(hit => hit.Name));
        Assert.Equal(["search.md"], backslashResult.Hits.Select(hit => hit.Name));
    }

    [Fact]
    public void Search_BestMatch_UsesAnyTermUnlessRequiredAndSupportsMatchAll()
    {
        using var folder = new SearchTestFolder();
        folder.WriteNote(
            "Project.md",
            "project overview archived",
            modifiedDaysAgo: 3);
        folder.WriteNote(
            "Detailed.md",
            "project beta implementation",
            modifiedDaysAgo: 2);
        folder.WriteNote(
            "BetaOnly.md",
            "beta reference",
            modifiedDaysAgo: 1);
        folder.WriteNote(
            "Unrelated.md",
            "cooking notes",
            modifiedDaysAgo: 0);
        folder.UpdateIndex();

        var ranked = NoteSearchIndexService.Search(
            folder.Path,
            "~ project beta",
            maxResults: 100,
            CancellationToken.None);
        var required = NoteSearchIndexService.Search(
            folder.Path,
            "~ +project beta",
            maxResults: 100,
            CancellationToken.None);
        var excluded = NoteSearchIndexService.Search(
            folder.Path,
            "~ project -beta",
            maxResults: 100,
            CancellationToken.None);
        var globalExclusion = NoteSearchIndexService.Search(
            folder.Path,
            "~ project beta -archived",
            maxResults: 100,
            CancellationToken.None);
        var allRanked = NoteSearchIndexService.Search(
            folder.Path,
            "~ * project",
            maxResults: 100,
            CancellationToken.None);
        var grouped = NoteSearchIndexService.Search(
            folder.Path,
            "(project OR cooking) NOT archived",
            maxResults: 100,
            CancellationToken.None);

        Assert.Equal(
            ["Detailed.md", "Project.md", "BetaOnly.md"],
            ranked.Hits.Select(hit => hit.Name));
        Assert.Equal(
            ["Detailed.md", "Project.md"],
            required.Hits.Select(hit => hit.Name));
        Assert.Equal(["Project.md"], excluded.Hits.Select(hit => hit.Name));
        Assert.Equal(
            ["Detailed.md", "BetaOnly.md"],
            globalExclusion.Hits.Select(hit => hit.Name));
        Assert.Equal(4, allRanked.Hits.Count);
        Assert.Equal(
            ["Project.md", "Detailed.md", "Unrelated.md", "BetaOnly.md"],
            allRanked.Hits.Select(hit => hit.Name));
        Assert.Equal(
            ["Unrelated.md", "Detailed.md"],
            grouped.Hits.Select(hit => hit.Name));
    }

    [Fact]
    public void Search_FieldOperatorsAndWeights_AffectMembershipAndRanking()
    {
        using var folder = new SearchTestFolder();
        folder.WriteNote(
            "Priority.md",
            "ordinary body",
            modifiedDaysAgo: 3);
        folder.WriteNote(
            "Body.md",
            """
            tags:
              - active

            priority details
            """,
            modifiedDaysAgo: 1);
        folder.UpdateIndex();

        var name = NoteSearchIndexService.Search(
            folder.Path,
            "name:prior",
            maxResults: 100,
            CancellationToken.None);
        var titleAlias = NoteSearchIndexService.Search(
            folder.Path,
            "title:prior",
            maxResults: 100,
            CancellationToken.None);
        var tag = NoteSearchIndexService.Search(
            folder.Path,
            "tag:active",
            maxResults: 100,
            CancellationToken.None);
        var ranked = NoteSearchIndexService.Search(
            folder.Path,
            "~ priority",
            maxResults: 100,
            CancellationToken.None);

        Assert.Equal(["Priority.md"], name.Hits.Select(hit => hit.Name));
        Assert.Equal(
            ["Priority.md"],
            titleAlias.Hits.Select(hit => hit.Name));
        Assert.Equal(["Body.md"], tag.Hits.Select(hit => hit.Name));
        Assert.Equal("Priority.md", ranked.Hits[0].Name);
    }

    [Fact]
    public void Search_IsCaseAndDiacriticInsensitive()
    {
        using var folder = new SearchTestFolder();
        folder.WriteNote(
            "Unicode.md",
            "A CAFÉ project summary",
            modifiedDaysAgo: 1);
        folder.UpdateIndex();

        var result = NoteSearchIndexService.Search(
            folder.Path,
            "cafe",
            maxResults: 100,
            CancellationToken.None);

        Assert.Equal(["Unicode.md"], result.Hits.Select(hit => hit.Name));
    }

    [Fact]
    public void UpdateIndex_RebuildsAnOlderDisposableSchema()
    {
        using var folder = new SearchTestFolder();
        folder.WriteNote(
            "Migrated.md",
            "schema migration needle",
            modifiedDaysAgo: 1);
        var databasePath = NoteSearchIndexService.GetDatabasePath(folder.Path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException());
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = databasePath,
                       Pooling = false
                   }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE stale_search_marker (value TEXT);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        folder.UpdateIndex();

        var result = NoteSearchIndexService.Search(
            folder.Path,
            "migration needle",
            maxResults: 100,
            CancellationToken.None);
        using var verification = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        verification.Open();
        using var versionCommand = verification.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";

        Assert.Equal(2L, (long)(versionCommand.ExecuteScalar() ?? -1L));
        Assert.Equal(["Migrated.md"], result.Hits.Select(hit => hit.Name));
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

    private sealed class SearchTestFolder : IDisposable
    {
        public SearchTestFolder()
        {
            Path = Directory.CreateDirectory(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"NoteManager.SearchTests.{Guid.NewGuid():N}")).FullName;
        }

        public string Path { get; }

        public void WriteNote(
            string relativePath,
            string content,
            int modifiedDaysAgo)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException());
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(
                path,
                DateTime.UtcNow.AddDays(-modifiedDaysAgo));
        }

        public void UpdateIndex()
            => NoteSearchIndexService.UpdateIndex(
                Path,
                progress: null,
                CancellationToken.None);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
