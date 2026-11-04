# Contributing

Issues and pull requests are welcome. Bug reports and feature requests have templates. Security reports should stay private; see [SECURITY.md](SECURITY.md). The code of conduct is [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## Prerequisites

### .NET SDK

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download). `global.json` pins `10.0.100` with `rollForward` set to `latestFeature`, so a later 10.0 SDK is accepted. `dotnet --version` should print a 10.0 version.

### C compiler

The tree-sitter grammars compile into one native library during `dotnet build`. The script exits 0 with a message when it cannot find a compiler, and the managed build still succeeds. Queries against TypeScript, JavaScript, Python, and the C# fallback then return no symbols.

**Windows.** Visual Studio or the Visual Studio Build Tools, with the MSVC C++ tools (`Microsoft.VisualStudio.Component.VC.Tools.x86.x64`). `src/Symdex.TreeSitter/scripts/build-native.ps1` finds the install with `vswhere`, runs `vcvars64.bat`, and compiles with `cl.exe`.

**Linux and macOS.** `cc`, `clang`, or `gcc` on `PATH`. `src/Symdex.TreeSitter/scripts/build-native.sh` uses `$CC` when it is set and names a compiler, otherwise the first of those three it can run.

## Build and test

```bash
dotnet build symdex.sln
dotnet test symdex.sln
```

One test:

```bash
dotnet test tests/Symdex.Tests/Symdex.Tests.csproj --filter "FullyQualifiedName~SampleRepoTests.Refs_finds_implementation_and_usage_sites_across_files"
```

Run the CLI from the checkout:

```bash
dotnet run --project src/Symdex.Cli -- outline <file>
dotnet run --project src/Symdex.Cli -- find <name>
```

`tests/fixtures/sample-repo` has its own `Directory.Build.props` so it is not built as part of symdex. Tests copy a fixture to a temp directory before indexing, because the index is written to `.index/` inside the repo being scanned. The C# fixture is restored in that copy. `MSBuildWorkspace` cannot finish loading a project that has package references and has not been restored.

Pack a local tool and smoke-test it:

```bash
dotnet pack src/Symdex.Cli/Symdex.Cli.csproj -c Release -o artifacts/package
```

Windows: `./scripts/smoke-test.ps1`. Everything else: `bash scripts/smoke-test.sh`. Both install the `.nupkg` into a temp tool directory and run it against copies of the fixtures.

## Gotchas

- Do not create a directory named `native` under `src/Symdex.TreeSitter`. The build output directory is `native-build/` on purpose. On a case-insensitive filesystem, `native` collides with `NativeInterop/` and a clean of the output directory has deleted those sources.
- Pattern order in `queries/csharp.scm`, `queries/typescript.scm`, and `queries/python.scm` is the contract with `CSharpPatternKinds`, `TypeScriptPatternKinds`, and `PythonPatternKinds` in `GrammarRegistry.cs`. A match is classified by `pattern_index`, not by the capture name. Reordering a pattern without editing the array mis-labels every following symbol.
- If a build fails with a stale MSBuild or VBCSCompiler error after an SDK or package change, run `dotnet build-server shutdown` and build again. An old build server can keep the previous assemblies loaded.
- Read [docs/design-decisions.md](docs/design-decisions.md) before changing SDK registration, the SQLite connection string, the `.js`/`.jsx` grammar mapping, `find`, or symbol resolution. [AGENTS.md](AGENTS.md) is the short map of the projects (the same text is in `CLAUDE.md`).

## Commits

History follows [Conventional Commits](https://www.conventionalcommits.org/):

```text
type(scope): subject
```

The scope is optional. Types already in the log include `feat`, `fix`, `docs`, `test`, `refactor`, `build`, `ci`, and `chore`. The subject is imperative and lowercase, with no trailing period.

```text
feat(treesitter): add Python feeder
fix(roslyn): open projects with the SDK that restored them
docs: MCP client configuration
```

## Pull requests

Describe why the change is needed, not only what moved. Keep a pull request to one concern when that is practical.

Run `dotnet test symdex.sln` and paste the command into the pull request. The template also asks you to confirm the gotchas above: no `native` directory, `.scm` pattern order still matches `GrammarRegistry`, and a stale build server was shut down if you hit that error.

Behavior changes belong in [CHANGELOG.md](CHANGELOG.md) under `Unreleased`.

## Adding a language

Python is the model (`feat(treesitter): vendor tree-sitter-python v0.25.0`, then `feat(treesitter): add Python feeder`, then `test: python fixture`). The vendor scripts were added immediately after, in `chore: grammar provenance manifest and vendor script`, and they encode the file set that commit copied. Use the scripts for the next language.

1. **Register the grammar with both vendor scripts, then run one.** `src/Symdex.TreeSitter/scripts/vendor-grammar.ps1` only accepts names in its `ValidateSet`, and `vendor-grammar.sh` only accepts names in its `case`. Add the upstream repo, the destination directory under `grammars/`, and the file list to both. Python's entry copies `LICENSE`, `src/parser.c`, `src/scanner.c`, `src/node-types.json`, and `src/tree_sitter/parser.h`, `array.h`, and `alloc.h` from tag `v0.25.0` (commit `293fdc02038ee2bf0e2e206711b69c90ac0d413f`).

   From `src/Symdex.TreeSitter`:

   ```bash
   sh scripts/vendor-grammar.sh <name> <tag>
   ```

   ```powershell
   ./scripts/vendor-grammar.ps1 -Name <name> -Tag <tag>
   ```

   The script prints the tag, commit, file list, and the grammar's `LANGUAGE_VERSION`. That version has to sit in the range the vendored runtime accepts (13 through 15; see [grammars/GRAMMARS.md](src/Symdex.TreeSitter/grammars/GRAMMARS.md)). Python is 15. A grammar outside that range needs a runtime upgrade first.

2. **Update `grammars/GRAMMARS.md`.** The script does not edit it. Record the upstream repo, tag, commit, license, ABI, and file list it printed.

3. **Compile the new sources.** Add `parser.c` and, when the grammar has one, `scanner.c` to the source list in `scripts/build-native.ps1` and to the `compile` lines in `scripts/build-native.sh`. Pass an include directory of that grammar's `src`, the way the Python lines do. Leave `scripts/exports.def` alone. It lists the tree-sitter runtime API for MSVC. Grammar entry points export themselves, and adding `tree_sitter_<lang>` to the `.def` file produces a duplicate-export warning. Python's commit did not touch `exports.def`.

4. **Declare the entry point.** In `NativeInterop/NativeMethods.cs`, add a `[LibraryImport]` method whose name is the symbol the grammar exports. Python added `tree_sitter_python()`.

5. **Add the query and the pattern-kind array together.** Create `queries/<lang>.scm`. It is embedded automatically (`queries\*.scm` in the tree-sitter project). The feeder understands two shapes:

   - A declaration pattern with captures `@name` and `@def`. The kind string in the array is a symbol kind (`class`, `function`, ...).
   - A pattern whose kind string is `import`, `call`, or `reference`, with capture `@source`. Those become reference rows. The text of `@source` is the key, so `refs` later matches it against a symbol name.

   Add a `PatternKinds` array in `GrammarRegistry.cs` in the same order as the patterns, and a `ForFile` arm that loads the grammar. Python is `queries/python.scm`, `PythonPatternKinds`, and the `.py` / `.pyi` arms. `function_definition` is tagged `function` in the array; the feeder reclassifies it (step 7).

6. **Map the extension.** Add a constant to `Languages` in `src/Symdex.Core/Models.cs` (`Python = "python"`). Include it in `TreeSitterFeeder.CanHandle`. Map each extension in `RepoScanner.ExtensionToLanguage`. Python maps `.py` and `.pyi`. If the language has generated directories you should never index, add them to `SkippedDirNames`. Python added `__pycache__`, `.venv`, `venv`, `.tox`, `.mypy_cache`, and `site-packages`.

7. **Change `TreeSitterFeeder` only when the shared extractor is wrong for this grammar.** It already walks whatever `GrammarRegistry` returns. Python needed extra rules: a function sitting directly in a class body is stored as a method, a definition nested under `decorated_definition` is dropped so the decorated span wins, and a docstring is stored as the doc comment. A language whose declarations are ordinary named nodes, and whose calls fit the `@source` patterns, does not need a feeder change.

8. **Roslyn stays C#-only.** `RoslynFeeder.CanHandle` returns true only for `csharp`, and `IndexerFactory` already constructs Roslyn first and tree-sitter second. A new language does not get a new project. Update the comment on `IndexerFactory` so it still lists which languages are tree-sitter only.

9. **Add a fixture and tests.** Python's fixture is `tests/fixtures/python-sample` (`calc.py`, `app.py`, `stubs.pyi`). `PythonFeederTests` feeds one buffer and checks the class, method, function, assignment, and docstring. `PythonSampleTests` copies the fixture to a temp directory and checks outline spans, `find` across `.py` and `.pyi`, a call in another file, and that editing one file reindexes only that file. Copy fixtures in the test. Indexing writes `.index/` into the tree you point at.
