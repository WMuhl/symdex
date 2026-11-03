# Design decisions

Notes for anyone changing the indexer. Several of these look like bugs until the constraint is visible.

## Solution files are classic `.sln`

symdex's own solution, and the solution it looks for in a target repo, are classic `.sln` files. The XML `.slnx` format needs a newer SDK than this project is willing to require of every target repository.

## Roslyn loads the whole solution and writes changed files

Cross-file references and implementations come from `SymbolFinder` on a full compilation, so the Roslyn feeder opens the whole solution. It inserts and replaces database rows only for the files in the current request. Unchanged files keep their rows.

## When C# falls back to tree-sitter

The Roslyn feeder returns failure, and the indexer retries those files with tree-sitter, when any of these happen:

- `--solution` points at a missing file, or the repo root has more than one `.sln`, or it has no `.sln` and more than one `.csproj`, or it has neither.
- `MSBuildWorkspace` fails while opening the project.
- Any project in the solution has a compile error. One error aborts the Roslyn pass for that run.

A project that references NuGet packages needs `dotnet restore` first. Without `obj/project.assets.json` the workspace does not finish loading, the CLI prints a `symdex:` warning, and the `.cs` files are indexed with tree-sitter. An SDK-style project with no package references can compile without a restore; the sample fixture is in that category, and the tests restore it anyway so they match a normal repo.

The C# tree-sitter query records declarations and `using` directives. It does not record call expressions, and it does not store a fully-qualified name. A fallback index is therefore a syntactic outline plus using-directive hits, which is a different result from a Roslyn index of the same files.

## Which SDK Roslyn registers

`MSBuildLocator.RegisterDefaults()` follows the SDK that hosts the symdex process. A repo restored with a different SDK then compiles with no reference assemblies, and every project looks full of errors, which trips the fallback above.

`RoslynFeeder` asks `dotnet --version` with the target repo as the working directory (so that repo's `global.json` applies) and registers that SDK. `global.json` in this repository only pins the SDK used to build symdex: 10.0.100, rolling forward within the 10.0 feature band. The `Microsoft.CodeAnalysis.*` packages are on the 5.3 line, matching the compiler shipped with the .NET 10 SDK. The MSBuild assemblies referenced for compilation are 18.3 and are marked compile-time only, so the SDK's own MSBuild is what runs.

`MSBuildLocator` registration sits in its own method. The JIT must not load `MSBuildWorkspace` types before an MSBuild instance is registered, or the process binds the wrong assemblies.

## Published builds keep the assemblies on disk

A single-file publish leaves `Assembly.Location` empty. `Microsoft.Build.Locator` and `MSBuildWorkspace` then fail to find the SDK, and C# falls back to tree-sitter. Release 1.0.0 shipped self-contained single-file zips and hit that limit.

Current releases are a framework-dependent .NET tool, plus per-RID zips from `dotnet publish` with `PublishSingleFile` left false. The tool package puts `symdex_treesitter` under `runtimes/<rid>/native/`. The zips leave that library next to the executable and include the `skill/` folder. Roslyn still needs a .NET SDK on the machine, the one `dotnet --version` selects in the target repo, and a restored project when the project has package references.

## Resolving a name for `refs` and `callers`

Resolution tries an exact `fqname` match first. If that hits, the short-name lookup is skipped, so a fully-qualified name is not also treated as some other symbol's short name. Otherwise the distinct fully-qualified names of symbols with that short name are the candidates. Tree-sitter rows store a null `fqname`, and the key falls back to the bare name, so every definition of that name is one candidate. More than one candidate is printed and the process exits 2. Guessing would attach the reference list to the wrong symbol.

`callers` then keeps reference rows whose kind is `call` or `reference`. `refs` keeps every kind stored for that key, including `implementation`: Roslyn interface implementations, and TypeScript `extends` / `implements`.

## `find` uses both LIKE and FTS

`find` unions a `LIKE '%query%'` match on `name` and `fqname` with an FTS5 prefix-phrase match on `name`, `fqname`, and `doc`. The substring match is what makes `helper` return `typed_helper`. The full-text index is what makes `payments` return a symbol whose doc comment contains that word. The FTS query is wrapped as a quoted phrase with a trailing `*`, so punctuation in a name such as `Foo.Bar` is not read as FTS syntax. Doc comments are not part of the LIKE clause.

## `.js` and `.jsx` use the TypeScript grammars

The vendored grammars are tree-sitter-c-sharp, tree-sitter-typescript (typescript and tsx), and tree-sitter-python. There is no tree-sitter-javascript grammar in the tree. `.ts` and `.js` are parsed with the typescript grammar. `.tsx` and `.jsx` are parsed with the tsx grammar. Both grammars accept the JavaScript those extensions contain. The typescript and tsx grammars share `queries/typescript.scm`. JSX tag names are `identifier` nodes, so that query does not mention `jsx_*` types. Those node types exist only in the tsx grammar, and a pattern that names one fails to compile for `.ts` and `.js`.

## SQLite pooling is off

The connection string sets `Pooling=False`. Microsoft.Data.Sqlite pools connections by default and can keep the file handle open after `Dispose`. Callers need to delete `.index/` immediately after closing the database (`rebuild`, and the temp copies the tests make).

## Query pattern order is positional

Tree-sitter matches are classified by `pattern_index`, not by capture name. The order of patterns in `queries/csharp.scm`, `queries/typescript.scm`, and `queries/python.scm` has to match `CSharpPatternKinds`, `TypeScriptPatternKinds`, and `PythonPatternKinds` in `GrammarRegistry.cs`.
