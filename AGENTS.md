# AGENTS.md

Guidance for coding agents working in this repository.

## Commands

Requires the .NET 8 SDK (`global.json` pins the exact version - see "MSBuildLocator" below for why).

```bash
dotnet build symdex.sln                 # build everything
dotnet test symdex.sln                  # run all tests
dotnet test tests/Symdex.Tests/Symdex.Tests.csproj --filter "FullyQualifiedName~SampleRepoTests.Refs_finds_implementation_and_usage_sites_across_files"  # single test
dotnet run --project src/Symdex.Cli -- outline <file>       # run the CLI without publishing
dotnet run --project src/Symdex.Cli -- find <name> [--kind <kind>]
dotnet run --project src/Symdex.Cli -- refs <fqname-or-name>
dotnet run --project src/Symdex.Cli -- callers <fqname-or-name>
dotnet run --project src/Symdex.Cli -- rebuild
```

Global CLI flags: `--repo <path>` (default cwd), `--json`, `--limit <n>` (hard cap 200), `--solution <path>` (C# feeder only).

If a build ever fails with a stale MSBuild/VBCSCompiler error after switching SDKs or package
versions, run `dotnet build-server shutdown` first - lingering build server processes can hold
onto assemblies from a previous build.

## Architecture

Four projects feed a single SQLite database (`<indexed-repo>/.index/code.db`, schema in
`src/Symdex.Core/Schema.cs`: `files`, `symbols`, `refs`, `symbols_fts`).

- **Symdex.Core** - schema/DB access (`IndexDatabase`), file hashing, git-aware file discovery
  (`RepoScanner`), and the freshness/reindex orchestration (`Indexer`). Defines `ISymbolFeeder`
  (`Feeding/ISymbolFeeder.cs`) - the plug point both language feeders implement. Core has no
  dependency on Roslyn or tree-sitter; it only knows about the feeder interface.
- **Symdex.Roslyn** - `RoslynFeeder` (C#, via `MSBuildWorkspace`). Loads the *whole* solution
  (needed for correct cross-file `SymbolFinder.FindReferencesAsync`/`FindImplementationsAsync`
  results) but only persists DB rows for the files listed in the current `FeedRequest` -
  unchanged files keep their existing rows. Any workspace load failure or compile error anywhere
  aborts the whole feed (`Success=false` + a warning); `Indexer` then retries those files through
  tree-sitter. `MSBuildLocator` registration is isolated in its own method (`RoslynFeeder`) so the
  JIT never touches `MSBuildWorkspace` types before registration runs.
- **Symdex.TreeSitter** - `TreeSitterFeeder` (TS/JS always, C# fallback). Vendors the tree-sitter
  runtime + c-sharp/typescript/tsx grammars under `grammars/` (real generated `parser.c`, not
  submodules) and compiles them into one native library (`symdex_treesitter`) via a pre-build
  MSBuild target that shells out to `scripts/build-native.ps1` (MSVC) or `.sh` (cc/clang/gcc). The
  managed P/Invoke layer lives in `NativeInterop/` and `.scm` queries in `queries/`.
  - **Do not create a folder named `native` here.** The native build output directory is
    `native-build/` specifically to avoid colliding with `NativeInterop/` on case-insensitive
    filesystems (Windows/macOS) - this collided once already and silently deleted source files.
  - `queries/csharp.scm` / `queries/typescript.scm` pattern order must stay positionally in sync
    with `CSharpPatternKinds`/`TypeScriptPatternKinds` in `GrammarRegistry.cs` (matches are
    identified by `pattern_index`, not by capture name).
- **Symdex.Cli** - `System.CommandLine`-based verbs in `Commands/`. `IndexerFactory.Create()` is
  the one place the feeder list is assembled (Roslyn before tree-sitter for C#) - both `Program.cs`
  and `RebuildCommand` go through it.

Every verb calls `Indexer.EnsureFresh` first: hash candidate files, diff against `files.hash`,
re-run only the feeder(s) needed for added/changed/deleted files, print `reindexed N files` to
stderr if anything changed. There is no separate "build the index" step or command.

`tests/fixtures/sample-repo` has its own `Directory.Build.props` deliberately isolating it from
the root one (it's a target codebase for tests, not part of symdex's own build). `SampleRepoTests`
copies it to a temp dir per test (indexing writes `.index/`) and runs `dotnet restore` on the copy
before invoking Roslyn - `MSBuildWorkspace` cannot open an unrestored project.

See `README.md`'s "Design decisions" section for the reasoning behind several non-obvious choices
(SQLite `Pooling=False`, `MSBuildLocator` SDK pinning + `global.json`, the single-file-publish
Roslyn limitation, `.js`/`.jsx` grammar mapping, FTS+LIKE in `find`, symbol resolution/ambiguity
rules for `refs`/`callers`) - read it before "fixing" something that looks like a bug there.
