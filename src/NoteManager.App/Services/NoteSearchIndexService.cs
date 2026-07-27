using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NoteManager.App.Services;

public sealed record NoteSearchIndexProgress(
    int ProcessedFiles,
    int TotalFiles,
    int UpdatedFiles,
    int RemovedFiles,
    int FailedFiles);

public sealed record NoteSearchIndexResult(
    int TotalFiles,
    int UpdatedFiles,
    int RemovedFiles,
    int FailedFiles,
    string DatabasePath);

public sealed record NoteSearchQueryResult(
    IReadOnlySet<string> Paths,
    bool IsAvailable);

public static partial class NoteSearchIndexService
{
    private const int BatchSize = 200;
    private const string IndexFolderName = ".notes";
    private const string DatabaseFileName = "search.db";

    public static string GetDatabasePath(string folderPath)
        => Path.Combine(Path.GetFullPath(folderPath), IndexFolderName, DatabaseFileName);

    public static NoteSearchIndexResult UpdateIndex(
        string folderPath,
        IProgress<NoteSearchIndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var rootFolder = Path.GetFullPath(folderPath);
        if (!Directory.Exists(rootFolder))
        {
            throw new DirectoryNotFoundException($"Folder not found: {rootFolder}");
        }

        var indexFolder = Path.Combine(rootFolder, IndexFolderName);
        Directory.CreateDirectory(indexFolder);
        var databasePath = Path.Combine(indexFolder, DatabaseFileName);
        var files = EnumerateMarkdownFiles(rootFolder, indexFolder, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return new NoteSearchIndexResult(0, 0, 0, 0, databasePath);
        }

        using var connection = OpenConnection(databasePath, readOnly: false);
        InitializeDatabase(connection);

        var indexedFiles = ReadIndexedFiles(connection, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return new NoteSearchIndexResult(files.Length, 0, 0, 0, databasePath);
        }

        var currentPaths = files
            .Select(file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedPaths = indexedFiles.Keys
            .Where(path => !currentPaths.Contains(path))
            .ToArray();
        var changedFiles = files
            .Where(file => !indexedFiles.TryGetValue(file.FullPath, out var indexed)
                           || indexed.ModifiedUtcTicks != file.ModifiedUtcTicks
                           || indexed.Length != file.Length)
            .ToArray();

        var processedFiles = files.Length - changedFiles.Length;
        var updatedFiles = 0;
        var removedFiles = 0;
        var failedFiles = 0;

        if (removedPaths.Length > 0)
        {
            using var transaction = connection.BeginTransaction();
            using var deleteSearch = CreateDeleteSearchCommand(connection, transaction);
            using var deleteMetadata = CreateDeleteMetadataCommand(connection, transaction);

            foreach (var path in removedPaths)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new NoteSearchIndexResult(
                        files.Length,
                        updatedFiles,
                        removedFiles,
                        failedFiles,
                        databasePath);
                }

                DeleteIndexedNote(deleteSearch, deleteMetadata, path);
                removedFiles++;
            }

            transaction.Commit();
        }

        progress?.Report(new NoteSearchIndexProgress(
            processedFiles,
            files.Length,
            updatedFiles,
            removedFiles,
            failedFiles));

        foreach (var batch in changedFiles.Chunk(BatchSize))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new NoteSearchIndexResult(
                    files.Length,
                    updatedFiles,
                    removedFiles,
                    failedFiles,
                    databasePath);
            }

            using var transaction = connection.BeginTransaction();
            using var deleteSearch = CreateDeleteSearchCommand(connection, transaction);
            using var deleteMetadata = CreateDeleteMetadataCommand(connection, transaction);
            using var upsertMetadata = CreateUpsertMetadataCommand(connection, transaction);
            using var insertSearch = CreateInsertSearchCommand(connection, transaction);

            foreach (var file in batch)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new NoteSearchIndexResult(
                        files.Length,
                        updatedFiles,
                        removedFiles,
                        failedFiles,
                        databasePath);
                }

                try
                {
                    var markdown = File.ReadAllText(file.FullPath);
                    var tags = string.Join(' ', MarkdownMetadataParser.ParseTags(markdown));

                    DeleteIndexedNote(deleteSearch, deleteMetadata, file.FullPath);
                    UpsertMetadata(upsertMetadata, file, tags);
                    InsertSearchDocument(insertSearch, file, tags, markdown);
                    updatedFiles++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    DeleteIndexedNote(deleteSearch, deleteMetadata, file.FullPath);
                    failedFiles++;
                }

                processedFiles++;
            }

            transaction.Commit();
            progress?.Report(new NoteSearchIndexProgress(
                processedFiles,
                files.Length,
                updatedFiles,
                removedFiles,
                failedFiles));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new NoteSearchIndexResult(
                files.Length,
                updatedFiles,
                removedFiles,
                failedFiles,
                databasePath);
        }

        using (var optimize = connection.CreateCommand())
        {
            optimize.CommandText = "PRAGMA optimize;";
            optimize.ExecuteNonQuery();
        }

        return new NoteSearchIndexResult(
            files.Length,
            updatedFiles,
            removedFiles,
            failedFiles,
            databasePath);
    }

    public static NoteSearchQueryResult Search(
        string folderPath,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var databasePath = GetDatabasePath(folderPath);
        if (!File.Exists(databasePath))
        {
            return new NoteSearchQueryResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                IsAvailable: false);
        }

        var ftsQuery = CreateFtsQuery(query);
        if (ftsQuery.Length == 0)
        {
            return new NoteSearchQueryResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                IsAvailable: true);
        }

        try
        {
            using var connection = OpenConnection(databasePath, readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText =
                """
                SELECT path
                FROM note_search
                WHERE note_search MATCH $query
                ORDER BY bm25(note_search, 0.0, 6.0, 2.0, 4.0, 1.0)
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", ftsQuery);
            command.Parameters.AddWithValue("$limit", maxResults);

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                paths.Add(reader.GetString(0));
            }

            return new NoteSearchQueryResult(paths, IsAvailable: true);
        }
        catch (SqliteException)
        {
            return new NoteSearchQueryResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                IsAvailable: false);
        }
    }

    private static MarkdownFileSnapshot[] EnumerateMarkdownFiles(
        string rootFolder,
        string indexFolder,
        CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false
        };
        var indexPrefix = Path.GetFullPath(indexFolder)
                          + Path.DirectorySeparatorChar;

        var files = new List<MarkdownFileSnapshot>();
        foreach (var path in Directory.EnumerateFiles(rootFolder, "*.md", options))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(indexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(fullPath);
            files.Add(new MarkdownFileSnapshot(
                fullPath,
                Path.GetRelativePath(rootFolder, fullPath),
                info.Name,
                info.LastWriteTimeUtc.Ticks,
                info.Length));
        }

        return files.ToArray();
    }

    private static SqliteConnection OpenConnection(string databasePath, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = readOnly ? 2 : 5
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void InitializeDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS indexed_notes (
                path TEXT PRIMARY KEY COLLATE NOCASE,
                relative_path TEXT NOT NULL,
                title TEXT NOT NULL,
                tags TEXT NOT NULL,
                modified_utc_ticks INTEGER NOT NULL,
                file_length INTEGER NOT NULL
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS note_search USING fts5(
                path UNINDEXED,
                title,
                relative_path,
                tags,
                content,
                tokenize = 'unicode61 remove_diacritics 2'
            );

            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, IndexedFileSnapshot> ReadIndexedFiles(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, modified_utc_ticks, file_length FROM indexed_notes;";

        var indexed = new Dictionary<string, IndexedFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            indexed[reader.GetString(0)] = new IndexedFileSnapshot(
                reader.GetInt64(1),
                reader.GetInt64(2));
        }

        return indexed;
    }

    private static SqliteCommand CreateDeleteSearchCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM note_search WHERE path = $path;";
        command.Parameters.Add("$path", SqliteType.Text);
        return command;
    }

    private static SqliteCommand CreateDeleteMetadataCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM indexed_notes WHERE path = $path;";
        command.Parameters.Add("$path", SqliteType.Text);
        return command;
    }

    private static SqliteCommand CreateUpsertMetadataCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO indexed_notes (
                path,
                relative_path,
                title,
                tags,
                modified_utc_ticks,
                file_length
            )
            VALUES (
                $path,
                $relativePath,
                $title,
                $tags,
                $modifiedUtcTicks,
                $fileLength
            );
            """;
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$relativePath", SqliteType.Text);
        command.Parameters.Add("$title", SqliteType.Text);
        command.Parameters.Add("$tags", SqliteType.Text);
        command.Parameters.Add("$modifiedUtcTicks", SqliteType.Integer);
        command.Parameters.Add("$fileLength", SqliteType.Integer);
        return command;
    }

    private static SqliteCommand CreateInsertSearchCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO note_search (
                path,
                title,
                relative_path,
                tags,
                content
            )
            VALUES (
                $path,
                $title,
                $relativePath,
                $tags,
                $content
            );
            """;
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$title", SqliteType.Text);
        command.Parameters.Add("$relativePath", SqliteType.Text);
        command.Parameters.Add("$tags", SqliteType.Text);
        command.Parameters.Add("$content", SqliteType.Text);
        return command;
    }

    private static void DeleteIndexedNote(
        SqliteCommand deleteSearch,
        SqliteCommand deleteMetadata,
        string path)
    {
        deleteSearch.Parameters["$path"].Value = path;
        deleteSearch.ExecuteNonQuery();
        deleteMetadata.Parameters["$path"].Value = path;
        deleteMetadata.ExecuteNonQuery();
    }

    private static void UpsertMetadata(
        SqliteCommand command,
        MarkdownFileSnapshot file,
        string tags)
    {
        command.Parameters["$path"].Value = file.FullPath;
        command.Parameters["$relativePath"].Value = file.RelativePath;
        command.Parameters["$title"].Value = file.Title;
        command.Parameters["$tags"].Value = tags;
        command.Parameters["$modifiedUtcTicks"].Value = file.ModifiedUtcTicks;
        command.Parameters["$fileLength"].Value = file.Length;
        command.ExecuteNonQuery();
    }

    private static void InsertSearchDocument(
        SqliteCommand command,
        MarkdownFileSnapshot file,
        string tags,
        string markdown)
    {
        command.Parameters["$path"].Value = file.FullPath;
        command.Parameters["$title"].Value = file.Title;
        command.Parameters["$relativePath"].Value = file.RelativePath;
        command.Parameters["$tags"].Value = tags;
        command.Parameters["$content"].Value = markdown;
        command.ExecuteNonQuery();
    }

    private static string CreateFtsQuery(string query)
    {
        var tokens = SearchTokenRegex()
            .Matches(query)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(token => $"\"{token.Replace("\"", "\"\"")}\"*");
        return string.Join(" AND ", tokens);
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex SearchTokenRegex();

    private sealed record MarkdownFileSnapshot(
        string FullPath,
        string RelativePath,
        string Title,
        long ModifiedUtcTicks,
        long Length);

    private sealed record IndexedFileSnapshot(
        long ModifiedUtcTicks,
        long Length);
}
