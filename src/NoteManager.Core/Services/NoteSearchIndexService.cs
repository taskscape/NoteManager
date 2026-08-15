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

public sealed record NoteSearchHit(
    string Path,
    string Name,
    string RelativePath,
    double RelevanceScore,
    int MatchedPositiveTermCount,
    long ModifiedUtcTicks);

public sealed record NoteSearchQueryResult(
    IReadOnlyList<NoteSearchHit> Hits,
    NoteSearchMode Mode,
    bool IsAvailable,
    string? Error = null,
    bool IsCanceled = false);

public static class NoteSearchIndexService
{
    private const int BatchSize = 200;
    private const int SchemaVersion = 2;
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
            using var insertLiteralSearch =
                CreateInsertLiteralSearchCommand(connection, transaction);

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
                    InsertSearchDocument(
                        insertSearch,
                        insertLiteralSearch,
                        file,
                        tags,
                        markdown);
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
        var parseResult = NoteSearchQueryParser.Parse(query);
        if (!parseResult.IsValid)
        {
            return new NoteSearchQueryResult(
                [],
                NoteSearchMode.Strict,
                IsAvailable: true,
                parseResult.Error);
        }

        return Search(
            folderPath,
            parseResult.Query!,
            maxResults,
            cancellationToken);
    }

    public static NoteSearchQueryResult Search(
        string folderPath,
        ParsedNoteSearchQuery query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        var databasePath = GetDatabasePath(folderPath);
        if (!File.Exists(databasePath))
        {
            return new NoteSearchQueryResult(
                [],
                query.Mode,
                IsAvailable: false);
        }

        if (query.IsEmpty)
        {
            return new NoteSearchQueryResult(
                [],
                query.Mode,
                IsAvailable: true);
        }

        try
        {
            using var connection = OpenConnection(databasePath, readOnly: true);
            var documents = ReadSearchDocuments(connection, cancellationToken);
            if (documents is null)
            {
                return CanceledSearchResult(query.Mode);
            }

            var terms = NoteSearchQueryParser
                .EnumerateTerms(query.Root)
                .Concat(query.RequiredExpressions.SelectMany(
                    expression => NoteSearchQueryParser.EnumerateTerms(expression)))
                .Concat(query.ExcludedExpressions.SelectMany(
                    expression => NoteSearchQueryParser.EnumerateTerms(expression)))
                .Distinct()
                .ToArray();
            var termMatches =
                new Dictionary<NoteSearchTerm, IReadOnlyDictionary<string, TermMatch>>();
            foreach (var term in terms)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return CanceledSearchResult(query.Mode);
                }

                var matches = term.IsMatchAll
                    ? documents.Values.ToDictionary(
                        document => document.Path,
                        _ => new TermMatch(0),
                        StringComparer.OrdinalIgnoreCase)
                    : SearchTerm(connection, term, cancellationToken);
                if (matches is null)
                {
                    return CanceledSearchResult(query.Mode);
                }

                termMatches[term] = matches;
            }

            var positiveTerms = NoteSearchQueryParser
                .EnumerateTerms(query.Root, includeNegated: false)
                .Concat(query.RequiredExpressions.SelectMany(
                    expression => NoteSearchQueryParser.EnumerateTerms(
                        expression,
                        includeNegated: false)))
                .Where(term => !term.IsMatchAll)
                .Distinct()
                .ToArray();

            var hits = new List<NoteSearchHit>();
            foreach (var document in documents.Values)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return CanceledSearchResult(query.Mode);
                }

                if (!Evaluate(query.Root!, document.Path, termMatches)
                    || query.RequiredExpressions.Any(required =>
                        !Evaluate(required, document.Path, termMatches))
                    || query.ExcludedExpressions.Any(excluded =>
                        Evaluate(excluded, document.Path, termMatches)))
                {
                    continue;
                }

                var matchedPositiveTerms = positiveTerms
                    .Where(term => termMatches[term].ContainsKey(document.Path))
                    .ToArray();
                var relevance = matchedPositiveTerms.Sum(
                    term => termMatches[term][document.Path].Score);
                relevance += matchedPositiveTerms.Length * 10;
                relevance += CountSatisfiedAndGroups(
                                 query.Root!,
                                 document.Path,
                                 termMatches)
                             * 0.25;

                hits.Add(new NoteSearchHit(
                    document.Path,
                    document.Name,
                    document.RelativePath,
                    relevance,
                    matchedPositiveTerms.Length,
                    document.ModifiedUtcTicks));
            }

            IOrderedEnumerable<NoteSearchHit> ordered = query.Mode switch
            {
                NoteSearchMode.BestMatch => hits
                    .OrderByDescending(hit => hit.RelevanceScore)
                    .ThenByDescending(hit => hit.MatchedPositiveTermCount)
                    .ThenByDescending(hit => hit.ModifiedUtcTicks),
                _ => hits.OrderByDescending(hit => hit.ModifiedUtcTicks)
            };
            var orderedHits = ordered
                .ThenBy(hit => hit.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(hit => hit.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToArray();

            return new NoteSearchQueryResult(
                orderedHits,
                query.Mode,
                IsAvailable: true);
        }
        catch (SqliteException)
        {
            return new NoteSearchQueryResult(
                [],
                query.Mode,
                IsAvailable: false);
        }
    }

    private static NoteSearchQueryResult CanceledSearchResult(NoteSearchMode mode)
        => new(
            [],
            mode,
            IsAvailable: true,
            IsCanceled: true);

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
            DefaultTimeout = readOnly ? 2 : 5,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void InitializeDatabase(SqliteConnection connection)
    {
        using (var configure = connection.CreateCommand())
        {
            configure.CommandText =
                """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                """;
            configure.ExecuteNonQuery();
        }

        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            var currentVersion = Convert.ToInt32(versionCommand.ExecuteScalar());
            if (currentVersion != SchemaVersion)
            {
                using var rebuild = connection.CreateCommand();
                rebuild.CommandText =
                    """
                    DROP TABLE IF EXISTS note_search;
                    DROP TABLE IF EXISTS note_literal_search;
                    DROP TABLE IF EXISTS indexed_notes;
                    """;
                rebuild.ExecuteNonQuery();
            }
        }

        using var create = connection.CreateCommand();
        create.CommandText =
            """
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

            CREATE VIRTUAL TABLE IF NOT EXISTS note_literal_search USING fts5(
                path UNINDEXED,
                title,
                relative_path,
                tags,
                content,
                tokenize = 'trigram'
            );

            PRAGMA user_version = 2;
            """;
        create.ExecuteNonQuery();
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
        command.CommandText =
            """
            DELETE FROM note_search WHERE path = $path;
            DELETE FROM note_literal_search WHERE path = $path;
            """;
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

    private static SqliteCommand CreateInsertLiteralSearchCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO note_literal_search (
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
        SqliteCommand literalCommand,
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

        literalCommand.Parameters["$path"].Value = file.FullPath;
        literalCommand.Parameters["$title"].Value =
            NoteSearchQueryParser.NormalizeLiteral(file.Title);
        literalCommand.Parameters["$relativePath"].Value =
            NoteSearchQueryParser.NormalizeLiteral(file.RelativePath);
        literalCommand.Parameters["$tags"].Value =
            NoteSearchQueryParser.NormalizeLiteral(tags);
        literalCommand.Parameters["$content"].Value =
            NoteSearchQueryParser.NormalizeLiteral(markdown);
        literalCommand.ExecuteNonQuery();
    }

    private static Dictionary<string, SearchDocument>? ReadSearchDocuments(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText =
            """
            SELECT path, title, relative_path, modified_utc_ticks
            FROM indexed_notes;
            """;

        var documents =
            new Dictionary<string, SearchDocument>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var document = new SearchDocument(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3));
            documents[document.Path] = document;
        }

        return documents;
    }

    private static IReadOnlyDictionary<string, TermMatch>? SearchTerm(
        SqliteConnection connection,
        NoteSearchTerm term,
        CancellationToken cancellationToken)
        => !term.IsPhrase && NoteSearchQueryParser.IsWordTerm(term.Text)
            ? SearchWordTerm(connection, term, cancellationToken)
            : SearchLiteralTerm(connection, term, cancellationToken);

    private static IReadOnlyDictionary<string, TermMatch>? SearchWordTerm(
        SqliteConnection connection,
        NoteSearchTerm term,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText =
            """
            SELECT
                path,
                title,
                relative_path,
                tags,
                content,
                bm25(note_search, 0.0, 6.0, 2.0, 4.0, 1.0)
            FROM note_search
            WHERE note_search MATCH $query;
            """;
        command.Parameters.AddWithValue(
            "$query",
            CreateWordFtsQuery(term));

        var matches =
            new Dictionary<string, TermMatch>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var score = ScoreWordMatch(reader, term);
            score += Math.Max(0, -reader.GetDouble(5));
            matches[reader.GetString(0)] = new TermMatch(score);
        }

        return matches;
    }

    private static IReadOnlyDictionary<string, TermMatch>? SearchLiteralTerm(
        SqliteConnection connection,
        NoteSearchTerm term,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        var columns = GetColumnNames(term.Field);
        var canUseLike = !term.Text.Contains('%')
                         && !term.Text.Contains('_');
        var conditions = columns.Select(column =>
            canUseLike
                ? $"{column} LIKE $pattern"
                : $"instr({column}, $literal) > 0");
        command.CommandText =
            $"""
             SELECT path, title, relative_path, tags, content
             FROM note_literal_search
             WHERE {string.Join(" OR ", conditions)};
             """;
        command.Parameters.AddWithValue("$literal", term.Text);
        command.Parameters.AddWithValue("$pattern", $"%{term.Text}%");

        var matches =
            new Dictionary<string, TermMatch>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var score = ScoreLiteralMatch(reader, term);
            if (score > 0)
            {
                matches[reader.GetString(0)] = new TermMatch(score);
            }
        }

        return matches;
    }

    private static string CreateWordFtsQuery(NoteSearchTerm term)
    {
        var escaped = term.Text.Replace("\"", "\"\"");
        var expression = $"\"{escaped}\"*";
        var column = GetColumnName(term.Field);
        return column is null
            ? expression
            : $"{column} : {expression}";
    }

    private static double ScoreWordMatch(
        SqliteDataReader reader,
        NoteSearchTerm term)
    {
        var score = 0d;
        foreach (var field in GetFieldValues(reader, term.Field))
        {
            var occurrences = CountWordPrefixOccurrences(
                NoteSearchQueryParser.NormalizeLiteral(field.Text),
                term.Text,
                out var hasExactMatch);
            if (occurrences == 0)
            {
                continue;
            }

            var frequencyMultiplier =
                1 + Math.Min(occurrences - 1, 2) * 0.15;
            var exactMultiplier = hasExactMatch ? 1.25 : 1;
            score += field.Weight * frequencyMultiplier * exactMultiplier;
        }

        return score;
    }

    private static double ScoreLiteralMatch(
        SqliteDataReader reader,
        NoteSearchTerm term)
    {
        var score = 0d;
        foreach (var field in GetFieldValues(reader, term.Field))
        {
            var occurrences = CountLiteralOccurrences(field.Text, term.Text);
            if (occurrences == 0)
            {
                continue;
            }

            var frequencyMultiplier =
                1 + Math.Min(occurrences - 1, 2) * 0.15;
            score += field.Weight * frequencyMultiplier;
        }

        return score * (term.IsPhrase ? 2 : 1.5);
    }

    private static int CountWordPrefixOccurrences(
        string text,
        string term,
        out bool hasExactMatch)
    {
        var count = 0;
        hasExactMatch = false;
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && !IsWordCharacter(text[index]))
            {
                index++;
            }

            var start = index;
            while (index < text.Length && IsWordCharacter(text[index]))
            {
                index++;
            }

            if (start == index)
            {
                continue;
            }

            var word = text[start..index];
            if (!word.StartsWith(term, StringComparison.Ordinal))
            {
                continue;
            }

            count++;
            hasExactMatch |= word.Length == term.Length;
        }

        return count;
    }

    private static int CountLiteralOccurrences(string text, string term)
    {
        var count = 0;
        var start = 0;
        while (start <= text.Length - term.Length)
        {
            var index = text.IndexOf(term, start, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            count++;
            start = index + Math.Max(1, term.Length);
        }

        return count;
    }

    private static bool IsWordCharacter(char character)
        => char.IsLetterOrDigit(character) || character == '_';

    private static IReadOnlyList<string> GetColumnNames(NoteSearchField field)
        => field switch
        {
            NoteSearchField.Name => ["title"],
            NoteSearchField.Tag => ["tags"],
            NoteSearchField.Path => ["relative_path"],
            NoteSearchField.Body => ["content"],
            _ => ["title", "relative_path", "tags", "content"]
        };

    private static string? GetColumnName(NoteSearchField field)
        => field switch
        {
            NoteSearchField.Name => "title",
            NoteSearchField.Tag => "tags",
            NoteSearchField.Path => "relative_path",
            NoteSearchField.Body => "content",
            _ => null
        };

    private static IEnumerable<SearchFieldValue> GetFieldValues(
        SqliteDataReader reader,
        NoteSearchField field)
    {
        if (field is NoteSearchField.Any or NoteSearchField.Name)
        {
            yield return new SearchFieldValue(reader.GetString(1), 6);
        }

        if (field is NoteSearchField.Any or NoteSearchField.Path)
        {
            yield return new SearchFieldValue(reader.GetString(2), 2);
        }

        if (field is NoteSearchField.Any or NoteSearchField.Tag)
        {
            yield return new SearchFieldValue(reader.GetString(3), 4);
        }

        if (field is NoteSearchField.Any or NoteSearchField.Body)
        {
            yield return new SearchFieldValue(reader.GetString(4), 1);
        }
    }

    private static bool Evaluate(
        NoteSearchExpression expression,
        string path,
        IReadOnlyDictionary<
            NoteSearchTerm,
            IReadOnlyDictionary<string, TermMatch>> matches)
        => expression switch
        {
            NoteSearchTerm term => matches[term].ContainsKey(path),
            NoteSearchAnd and => Evaluate(and.Left, path, matches)
                                 && Evaluate(and.Right, path, matches),
            NoteSearchOr or => Evaluate(or.Left, path, matches)
                               || Evaluate(or.Right, path, matches),
            NoteSearchNot not => !Evaluate(not.Operand, path, matches),
            _ => false
        };

    private static int CountSatisfiedAndGroups(
        NoteSearchExpression expression,
        string path,
        IReadOnlyDictionary<
            NoteSearchTerm,
            IReadOnlyDictionary<string, TermMatch>> matches)
        => expression switch
        {
            NoteSearchAnd and =>
                (Evaluate(and.Left, path, matches)
                 && Evaluate(and.Right, path, matches)
                    ? 1
                    : 0)
                + CountSatisfiedAndGroups(and.Left, path, matches)
                + CountSatisfiedAndGroups(and.Right, path, matches),
            NoteSearchOr or =>
                CountSatisfiedAndGroups(or.Left, path, matches)
                + CountSatisfiedAndGroups(or.Right, path, matches),
            NoteSearchNot not =>
                CountSatisfiedAndGroups(not.Operand, path, matches),
            _ => 0
        };

    private sealed record MarkdownFileSnapshot(
        string FullPath,
        string RelativePath,
        string Title,
        long ModifiedUtcTicks,
        long Length);

    private sealed record IndexedFileSnapshot(
        long ModifiedUtcTicks,
        long Length);

    private sealed record SearchDocument(
        string Path,
        string Name,
        string RelativePath,
        long ModifiedUtcTicks);

    private sealed record SearchFieldValue(string Text, double Weight);

    private sealed record TermMatch(double Score);
}
