namespace Symdex.Core;

/// <summary>SQL DDL for the per-repo index database. Applied once per connection via <see cref="IndexDatabase"/>.</summary>
public static class Schema
{
    public const int Version = 2;

    public const string Ddl = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS meta (
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS files (
          id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE,
          hash TEXT NOT NULL,
          lang TEXT NOT NULL,
          indexed_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS symbols (
          id INTEGER PRIMARY KEY,
          name TEXT NOT NULL,
          fqname TEXT,
          kind TEXT NOT NULL,
          signature TEXT,
          doc TEXT,
          file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          container TEXT
        );

        CREATE TABLE IF NOT EXISTS refs (
          id INTEGER PRIMARY KEY,
          symbol_fqname TEXT NOT NULL,
          file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
          line INTEGER NOT NULL,
          kind TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
        CREATE INDEX IF NOT EXISTS idx_symbols_fqname ON symbols(fqname);
        CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_symbols_dedupe ON symbols(
          file_id, name, ifnull(fqname, ''), kind, ifnull(signature, ''), start_line, end_line, ifnull(container, '')
        );
        CREATE INDEX IF NOT EXISTS idx_refs_fqname ON refs(symbol_fqname);
        CREATE INDEX IF NOT EXISTS idx_refs_file ON refs(file_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_refs_dedupe ON refs(symbol_fqname, file_id, line, kind);

        CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(
          name, fqname, doc, content='symbols', content_rowid='id'
        );

        CREATE TRIGGER IF NOT EXISTS symbols_ai AFTER INSERT ON symbols BEGIN
          INSERT INTO symbols_fts(rowid, name, fqname, doc) VALUES (new.id, new.name, new.fqname, new.doc);
        END;

        CREATE TRIGGER IF NOT EXISTS symbols_ad AFTER DELETE ON symbols BEGIN
          INSERT INTO symbols_fts(symbols_fts, rowid, name, fqname, doc) VALUES ('delete', old.id, old.name, old.fqname, old.doc);
        END;

        CREATE TRIGGER IF NOT EXISTS symbols_au AFTER UPDATE ON symbols BEGIN
          INSERT INTO symbols_fts(symbols_fts, rowid, name, fqname, doc) VALUES ('delete', old.id, old.name, old.fqname, old.doc);
          INSERT INTO symbols_fts(rowid, name, fqname, doc) VALUES (new.id, new.name, new.fqname, new.doc);
        END;
        """;
}
