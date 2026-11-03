using Microsoft.Data.Sqlite;

namespace Symdex.Core;

/// <summary>Owns the SQLite connection to a single repo's `.index/code.db` and all reads/writes against it.</summary>
public sealed class IndexDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    private IndexDatabase(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static string GetIndexDbPath(string repoRoot) =>
        Path.Combine(repoRoot, ".index", "code.db");

    public static IndexDatabase OpenOrCreate(string repoRoot)
    {
        var dbPath = GetIndexDbPath(repoRoot);
        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);

        // Pooling=False: Microsoft.Data.Sqlite pools connections by default, which can keep the
        // native file handle open after Dispose() - this repo's .index/ directory needs to be
        // deletable (rebuild, temp test fixtures) right after the connection is closed.
        var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = """
                CREATE TABLE IF NOT EXISTS meta (
                  key TEXT PRIMARY KEY,
                  value TEXT NOT NULL
                );
                """;
            meta.ExecuteNonQuery();
        }

        var database = new IndexDatabase(connection);
        if (database.ReadSchemaVersion() != Schema.Version)
        {
            database.Rebuild();
        }

        return database;
    }

    public void Rebuild()
    {
        using var drop = _connection.CreateCommand();
        drop.CommandText = """
            DROP TABLE IF EXISTS symbols_fts;
            DROP TABLE IF EXISTS refs;
            DROP TABLE IF EXISTS symbols;
            DROP TABLE IF EXISTS files;
            DROP TABLE IF EXISTS meta;
            """;
        drop.ExecuteNonQuery();

        using var ddl = _connection.CreateCommand();
        ddl.CommandText = Schema.Ddl;
        ddl.ExecuteNonQuery();

        using var version = _connection.CreateCommand();
        version.CommandText = """
            INSERT INTO meta (key, value) VALUES ('schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        version.Parameters.AddWithValue("$version", Schema.Version.ToString());
        version.ExecuteNonQuery();
    }

    private int ReadSchemaVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        var value = cmd.ExecuteScalar();
        return value is string text && int.TryParse(text, out var version) ? version : 0;
    }

    public IReadOnlyDictionary<string, FileRecord> GetAllFiles()
    {
        var result = new Dictionary<string, FileRecord>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, path, hash, lang, indexed_at FROM files;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var record = new FileRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4));
            result[record.Path] = record;
        }

        return result;
    }

    /// <summary>Symbol identity keys grouped by repo-relative file path.</summary>
    public Dictionary<string, HashSet<string>> GetSymbolIdentities()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.path, s.kind, s.fqname, s.name
            FROM symbols s
            JOIN files f ON f.id = s.file_id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (!result.TryGetValue(path, out var keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                result[path] = keys;
            }

            var fqName = reader.IsDBNull(2) ? null : reader.GetString(2);
            keys.Add(SymbolIdentity.Key(reader.GetString(1), fqName, reader.GetString(3)));
        }

        return result;
    }

    public long UpsertFile(string path, string hash, string lang, string indexedAtIso)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files (path, hash, lang, indexed_at) VALUES ($path, $hash, $lang, $indexedAt)
            ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, lang = excluded.lang, indexed_at = excluded.indexed_at
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$lang", lang);
        cmd.Parameters.AddWithValue("$indexedAt", indexedAtIso);
        return (long)cmd.ExecuteScalar()!;
    }

    public void DeleteFile(string path)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    public void ReplaceSymbolsAndRefs(long fileId, IReadOnlyList<SymbolRow> symbols, IReadOnlyList<RefRow> refs)
    {
        using var transaction = _connection.BeginTransaction();

        using (var deleteSymbols = _connection.CreateCommand())
        {
            deleteSymbols.Transaction = transaction;
            deleteSymbols.CommandText = "DELETE FROM symbols WHERE file_id = $fileId;";
            deleteSymbols.Parameters.AddWithValue("$fileId", fileId);
            deleteSymbols.ExecuteNonQuery();
        }

        DeleteRefs(fileId, transaction);

        foreach (var symbol in symbols)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO symbols (name, fqname, kind, signature, doc, file_id, start_line, end_line, container)
                VALUES ($name, $fqname, $kind, $signature, $doc, $fileId, $startLine, $endLine, $container);
                """;
            insert.Parameters.AddWithValue("$name", symbol.Name);
            insert.Parameters.AddWithValue("$fqname", (object?)symbol.FqName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$kind", symbol.Kind);
            insert.Parameters.AddWithValue("$signature", (object?)symbol.Signature ?? DBNull.Value);
            insert.Parameters.AddWithValue("$doc", (object?)symbol.Doc ?? DBNull.Value);
            insert.Parameters.AddWithValue("$fileId", fileId);
            insert.Parameters.AddWithValue("$startLine", symbol.StartLine);
            insert.Parameters.AddWithValue("$endLine", symbol.EndLine);
            insert.Parameters.AddWithValue("$container", (object?)symbol.Container ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        InsertRefs(fileId, refs, transaction);

        transaction.Commit();
    }

    private void DeleteRefs(long fileId, SqliteTransaction transaction)
    {
        using var deleteRefs = _connection.CreateCommand();
        deleteRefs.Transaction = transaction;
        deleteRefs.CommandText = "DELETE FROM refs WHERE file_id = $fileId;";
        deleteRefs.Parameters.AddWithValue("$fileId", fileId);
        deleteRefs.ExecuteNonQuery();
    }

    private void InsertRefs(long fileId, IReadOnlyList<RefRow> refs, SqliteTransaction transaction)
    {
        foreach (var reference in refs)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO refs (symbol_fqname, file_id, line, kind)
                VALUES ($fqname, $fileId, $line, $kind);
                """;
            insert.Parameters.AddWithValue("$fqname", reference.SymbolFqName);
            insert.Parameters.AddWithValue("$fileId", fileId);
            insert.Parameters.AddWithValue("$line", reference.Line);
            insert.Parameters.AddWithValue("$kind", reference.Kind);
            insert.ExecuteNonQuery();
        }
    }

    public void ReplaceRefs(long fileId, IReadOnlyList<RefRow> refs)
    {
        using var transaction = _connection.BeginTransaction();
        DeleteRefs(fileId, transaction);
        InsertRefs(fileId, refs, transaction);
        transaction.Commit();
    }

    public IReadOnlyList<SymbolRecord> Outline(string repoRelativeFilePath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.name, s.fqname, s.kind, s.signature, s.doc, f.path, s.start_line, s.end_line, s.container
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = $path
            ORDER BY s.start_line;
            """;
        cmd.Parameters.AddWithValue("$path", repoRelativeFilePath);
        return ReadSymbols(cmd);
    }

    public IReadOnlyList<RefRecord> AllRefs()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.symbol_fqname, r.kind, f.path, r.line
            FROM refs r
            JOIN files f ON f.id = r.file_id
            ORDER BY f.path, r.line, r.kind, r.symbol_fqname;
            """;
        var results = new List<RefRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new RefRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return results;
    }

    public IReadOnlyList<SymbolRecord> Find(string query, string? kind, int limit)
    {
        using var cmd = _connection.CreateCommand();
        var kindFilter = kind is null ? "" : "AND s.kind = $kind ";
        cmd.CommandText = $"""
            SELECT s.id, s.name, s.fqname, s.kind, s.signature, s.doc, f.path, s.start_line, s.end_line, s.container
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE (s.name LIKE $like OR s.fqname LIKE $like OR s.id IN (
                SELECT rowid FROM symbols_fts WHERE symbols_fts MATCH $ftsQuery
            ))
            {kindFilter}
            ORDER BY f.path, s.start_line
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$like", $"%{query}%");
        cmd.Parameters.AddWithValue("$ftsQuery", EscapeFtsQuery(query));
        cmd.Parameters.AddWithValue("$limit", limit + 1); // fetch one extra to detect truncation
        if (kind is not null)
        {
            cmd.Parameters.AddWithValue("$kind", kind);
        }

        return ReadSymbols(cmd);
    }

    /// <summary>
    /// Distinct symbol keys for a query. An exact <c>fqname</c> match wins over <c>name</c>;
    /// otherwise the distinct fqnames (or names, when fqname is null) of symbols with that name.
    /// </summary>
    public IReadOnlyList<string> ResolveSymbolCandidates(string? nameOrFqName)
    {
        if (string.IsNullOrEmpty(nameOrFqName))
        {
            return [];
        }

        var byFqName = ReadKeys("""
            SELECT DISTINCT fqname AS key
            FROM symbols
            WHERE fqname = $exact
            ORDER BY key;
            """, nameOrFqName);
        if (byFqName.Count > 0)
        {
            return byFqName;
        }

        return ReadKeys("""
            SELECT DISTINCT COALESCE(fqname, name) AS key
            FROM symbols
            WHERE name = $exact
            ORDER BY key;
            """, nameOrFqName);
    }

    private List<string> ReadKeys(string sql, string exact)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$exact", exact);
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public IReadOnlyList<RefRecord> RefsFor(string? symbolFqNameOrName, IReadOnlyCollection<string>? kindFilter, int limit)
    {
        // A missing symbol (tree-sitter rows with no fqname, or a resolution miss) is an empty
        // result. Binding null as the key makes Microsoft.Data.Sqlite throw "Value must be set."
        if (string.IsNullOrEmpty(symbolFqNameOrName))
        {
            return [];
        }

        var kindClause = "";
        if (kindFilter is { Count: > 0 })
        {
            var placeholders = string.Join(", ", kindFilter.Select((_, i) => $"$kind{i}"));
            kindClause = $"AND r.kind IN ({placeholders}) ";
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT r.symbol_fqname, r.kind, f.path, r.line
            FROM refs r
            JOIN files f ON f.id = r.file_id
            WHERE r.symbol_fqname = $key
            {kindClause}
            ORDER BY f.path, r.line, r.kind
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$key", symbolFqNameOrName);
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        if (kindFilter is { Count: > 0 })
        {
            var i = 0;
            foreach (var k in kindFilter)
            {
                cmd.Parameters.AddWithValue($"$kind{i}", k);
                i++;
            }
        }

        var results = new List<RefRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new RefRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return results;
    }

    private static IReadOnlyList<SymbolRecord> ReadSymbols(SqliteCommand cmd)
    {
        var results = new List<SymbolRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SymbolRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return results;
    }

    private static string EscapeFtsQuery(string query)
    {
        // Treat the query as a literal phrase so punctuation in symbol names (e.g. "Foo.Bar")
        // doesn't get parsed as FTS5 query syntax.
        var escaped = query.Replace("\"", "\"\"");
        return $"\"{escaped}\"*";
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
