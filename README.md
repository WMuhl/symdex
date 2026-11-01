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
  Symdex.TreeSitter/     tree-sitter feeder + vendored grammars (TS/JS, Python, and C# fallback)
skill/                  packaged agent skill wrapping the CLI
tests/                  xUnit tests + a small fixture repo used by CI
```

## Building

Requires the .NET 10 SDK.

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
  fairly recent SDK; `.sln` works with the .NET 10 SDK this project targets and with older CI
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
- **`.js`/`.jsx` grammar mapping**: only `tree-sitter-c-sharp` and `tree-sitter-typescript` are
  vendored (per the build instructions), and the latter ships two grammars - `typescript` (`.ts`)
  and `tsx` (`.tsx`/JSX). `.js` is parsed with the `typescript` grammar and `.jsx` with `tsx`; both
  are supersets of plain JS, so this covers the four extensions without vendoring a third grammar.
- **`global.json` pinning + Roslyn package versions**: `global.json` pins the SDK to 10.0.x
  (`rollForward: latestFeature`). `RoslynFeeder` registers the MSBuild instance from the SDK that
  `dotnet --version` resolves in the target repo (that repo's `global.json`, otherwise the latest
  installed SDK), so the workspace uses the same SDK that restore used.
  `MSBuildLocator.RegisterDefaults()` follows the host process instead, and a mismatch leaves the
  compilation with no reference assemblies. The `Microsoft.CodeAnalysis.*` packages are pinned to
  the 5.3.x line, matching the compiler bundled with the .NET 10 SDK.
- **Roslyn requires a prior `dotnet restore`**: `MSBuildWorkspace` cannot open an unrestored
  project (no `obj/project.assets.json`) - it doesn't restore on your behalf. A repo an agent is
  actively working in has normally already been restored as part of the developer's own workflow;
  if it hasn't, symdex just falls back to the tree-sitter feeder for `.cs` files, per the mandatory
  graceful-degradation behavior.
- **Known limitation: Roslyn under the published single-file binary.** The `release.yml` binary is
  published with `PublishSingleFile=true`, under which `Assembly.Location` for the main assembly
  is empty - a well-known single-file-deployment gotcha that trips up some path resolution inside
  `Microsoft.Build.Locator`/`MSBuildWorkspace`'s child "BuildHost" process launch. In that specific
  hosting mode the Roslyn feeder currently fails to load a solution and the CLI transparently falls
  back to the tree-sitter feeder for `.cs` files - correct results, just syntax-only for C# - which
  is exactly the mandatory graceful-degradation path already required for other Roslyn failures.
  Running from a normal `dotnet build`/`dotnet test` output (not single-file) is unaffected and was
  used for all correctness testing in this repo.

## License

MIT - see [LICENSE](LICENSE). Matches the license on every vendored dependency (tree-sitter core,
tree-sitter-c-sharp, tree-sitter-typescript are all MIT).
