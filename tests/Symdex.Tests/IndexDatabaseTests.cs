using Microsoft.Data.Sqlite;
using Symdex.Core;

namespace Symdex.Tests;

public sealed class IndexDatabaseTests
{
    [Fact]
    public void RefsFor_returns_empty_when_the_symbol_key_is_missing()
    {
        var root = Directory.CreateTempSubdirectory("symdex-db-").FullName;
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            Assert.Empty(db.ResolveSymbolCandidates(null));
            Assert.Empty(db.RefsFor(null, kindFilter: null, 50));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Duplicate_symbol_and_ref_rows_are_ignored()
    {
        var root = Directory.CreateTempSubdirectory("symdex-db-").FullName;
        try
        {
            using (var db = IndexDatabase.OpenOrCreate(root))
            {
                var fileId = db.UpsertFile("a.cs", "hash", "csharp", "t");
                var symbol = new SymbolRow("T", "N.T", "class", "class T", null, 1, 3, null);
                var reference = new RefRow("N.T", 2, "reference");
                db.ReplaceSymbolsAndRefs(fileId, [symbol, symbol], [reference, reference]);
                Assert.Single(db.Outline("a.cs"));
                Assert.Single(db.AllRefs());
            }

            using (var db = IndexDatabase.OpenOrCreate(root))
            {
                Assert.Single(db.GetAllFiles());
                Assert.Single(db.Outline("a.cs"));
                Assert.Single(db.AllRefs());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Database_without_a_schema_version_is_rebuilt()
    {
        var root = Directory.CreateTempSubdirectory("symdex-db-").FullName;
        try
        {
            var dbPath = IndexDatabase.GetIndexDbPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE files (
                      id INTEGER PRIMARY KEY,
                      path TEXT NOT NULL UNIQUE,
                      hash TEXT NOT NULL,
                      lang TEXT NOT NULL,
                      indexed_at TEXT NOT NULL
                    );
                    CREATE TABLE refs (
                      id INTEGER PRIMARY KEY,
                      symbol_fqname TEXT NOT NULL,
                      file_id INTEGER NOT NULL,
                      line INTEGER NOT NULL,
                      kind TEXT NOT NULL
                    );
                    INSERT INTO files (path, hash, lang, indexed_at) VALUES ('old.cs', 'h', 'csharp', 't');
                    INSERT INTO refs (symbol_fqname, file_id, line, kind) VALUES ('N.T', 1, 1, 'reference');
                    INSERT INTO refs (symbol_fqname, file_id, line, kind) VALUES ('N.T', 1, 1, 'reference');
                    """;
                cmd.ExecuteNonQuery();
            }

            using var db = IndexDatabase.OpenOrCreate(root);
            Assert.Empty(db.GetAllFiles());
            Assert.Empty(db.AllRefs());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
