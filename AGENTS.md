# AGENTS.md

Guidance for coding agents working in this repository.

## Commands

Requires the .NET 10 SDK (`global.json` pins SDK 10.0.100 with `rollForward: latestFeature`). Roslyn registers the SDK that `dotnet --version` resolves in the *target* repo, which is a separate choice; see [docs/design-decisions.md](docs/design-decisions.md).

```bash
dotnet build symdex.sln                 # build everything
dotnet test symdex.sln                  # run all tests
dotnet test tests/Symdex.Tests/Symdex.Tests.csproj --filter "FullyQualifiedName~SampleRepoTests.Refs_finds_implementation_and_usage_sites_across_files"  # single test
dotnet run --project src/Symdex.Cli -- outline <file>       # run the CLI without publishing
dotnet run --project src/Symdex.Cli -- find <name> [--kind <kind>]
dotnet run --project src/Symdex.Cli -- refs <fqname-or-name>
dotnet run --project src/Symdex.Cli -- callers <fqname-or-name>
dotnet run --project src/Symdex.Cli -- rebuild
dotnet run --project src/Symdex.Cli -- mcp
```

Global CLI flags: `--repo <path>` (default cwd), `--json`, `--limit <n>` (default 50, hard cap 200), `--solution <path>` (C# feeder only). `refs` and `callers` exit 2 when more than one fully-qualified symbol matches.

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
- **Symdex.TreeSitter** - `TreeSitterFeeder` (TypeScript/JavaScript and Python always, C# when
  Roslyn fails). Vendors the tree-sitter runtime and the c-sharp, typescript, tsx, and python
  grammars under `grammars/` (generated `parser.c`, not submodules; provenance in
  `grammars/GRAMMARS.md`) and compiles them into one native library (`symdex_treesitter`) via a
  pre-build MSBuild target that shells out to `scripts/build-native.ps1` (MSVC) or `.sh`
  (cc/clang/gcc). The managed P/Invoke layer lives in `NativeInterop/` and `.scm` queries in
  `queries/`.
  - **Do not create a folder named `native` here.** The native build output directory is
    `native-build/` specifically to avoid colliding with `NativeInterop/` on case-insensitive
    filesystems (Windows/macOS) - this collided once already and silently deleted source files.
  - `queries/csharp.scm`, `queries/typescript.scm`, and `queries/python.scm` must stay
    positionally in sync with `CSharpPatternKinds`, `TypeScriptPatternKinds`, and
    `PythonPatternKinds` in `GrammarRegistry.cs` (matches are identified by `pattern_index`, not
    by capture name).
- **Symdex.Cli** - `System.CommandLine` 2.0 verbs in `Commands/`, plus the MCP server in `Mcp/`.
  `IndexerFactory.Create()` is the one place the feeder list is assembled (Roslyn before
  tree-sitter for C#). The query commands and `RebuildCommand` use it. `symdex mcp` uses
  `CreatePersistent()` so the MSBuild workspace stays open for the process lifetime.

Every query calls `Indexer.EnsureFresh` first: hash candidate files, diff against `files.hash`,
re-run only the feeder(s) needed for added/changed/deleted files, print `reindexed N files` to
stderr if anything changed. `rebuild` drops the database and then runs the same freshness pass.
A query does not need a separate index step first.

`tests/fixtures/sample-repo` has its own `Directory.Build.props` deliberately isolating it from
the root one (it's a target codebase for tests, not part of symdex's own build). The tests copy
it to a temp dir (indexing writes `.index/`) and run `dotnet restore` on the copy. An SDK project
with no package references can still load; an unrestored `PackageReference` makes
`MSBuildWorkspace` fail and the indexer falls back to tree-sitter. The tests restore so the
fixture matches a normal repo.

See [docs/design-decisions.md](docs/design-decisions.md) for the reasoning behind several
non-obvious choices (SQLite `Pooling=False`, which SDK `MSBuildLocator` registers,
framework-dependent packaging so Roslyn can resolve MSBuild, `.js`/`.jsx` grammar mapping,
FTS+LIKE in `find`, symbol resolution and the exit-code-2 ambiguity rule) - read it before
changing one of those.
