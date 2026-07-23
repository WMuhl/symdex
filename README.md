# symdex

A local codebase index for coding agents. `symdex` indexes symbols, references, and file
outlines from a repo into a per-repo SQLite database (`.index/code.db`), so an agent can query
"where is this symbol defined / used" without grepping or reading whole files. Everything runs
locally: no network calls, no telemetry, no external services.

## Repository layout

```
src/
  Symdex.Cli/          console app (binary name: symdex)
  Symdex.Core/          schema, SQLite access, file hashing, freshness checking
  Symdex.Roslyn/         C# semantic feeder (MSBuildWorkspace + Roslyn)
  Symdex.TreeSitter/     tree-sitter feeder + vendored grammars (TS/JS, and C# fallback)
skill/                  packaged agent skill wrapping the CLI
tests/                  xUnit tests + a small fixture repo used by CI
```

## Building

Requires the .NET 8 SDK.

```bash
dotnet build symdex.sln
dotnet test symdex.sln
```

## Usage

```bash
symdex outline src/Foo.cs
symdex find IPaymentProcessor
symdex refs IPaymentProcessor
symdex callers ProcessPayment
symdex rebuild
```

Every verb runs a freshness check first: changed files are re-hashed and re-indexed inline
before the query runs. Nothing needs to be indexed manually.

## Design decisions

Where the build instructions were ambiguous, the simpler option was chosen:

- **Solution file format**: classic `.sln`, not the newer `.slnx` XML format. `.slnx` requires a
  fairly recent SDK; `.sln` works with the .NET 8 SDK this project targets and with older CI
  runners without extra setup.
- **Feeder fallback granularity**: the Roslyn feeder loads the whole solution/compilation (it
  needs full context to resolve cross-file references correctly) but only persists database rows
  for the files that actually changed on a given run — unchanged files keep their existing rows.
  If the workspace fails to load, the CLI falls back to indexing just the affected `.cs` files
  with the tree-sitter feeder for that run.
- **Symbol resolution for `refs`/`callers`**: a query first tries an exact match against
  `fqname`, then `name`. If more than one distinct symbol matches, all candidates are printed and
  the process exits with code 2 rather than guessing.
- **FTS vs LIKE in `find`**: both are used together (a `LIKE '%query%'` substring match unioned
  with an FTS5 prefix-phrase match) since the spec calls for "substring + FTS match" rather than
  one or the other.
