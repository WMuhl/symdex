# Design decisions

Notes for anyone changing the indexer. Several of these look like bugs until the constraint is visible.

## Which solution file is opened

`--solution` is used as given. Otherwise symdex takes the single `.sln` or `.slnx` in the repo root, or, if the root has no solution file, the single `.csproj` there. More than one solution file at the root, or more than one `.csproj` and no solution file, is an error and C# falls back to tree-sitter.

If the root has neither, symdex searches three directory levels down for `.sln` and `.slnx`, skipping `bin`, `obj`, and the other directories `RepoScanner` skips. One match is used. Several matches: a file whose name equals the repo directory name wins, otherwise the shallowest path, with path order as the tie-break. Stderr names the file that was chosen and says that `--solution` overrides it. If the search finds nothing, the warning tells you to pass `--solution <path>`.

`MSBuildWorkspace.OpenSolutionAsync` opens both `.sln` and `.slnx`. The .NET SDK that `dotnet --version` selects in the target repo is the MSBuild that parses the file, and that SDK has to understand `.slnx`.

## Roslyn loads the whole solution and writes changed files

Cross-file references come from the semantic model of a full compilation, so the Roslyn feeder opens the whole solution. It replaces symbol rows only for files in the current request. Unchanged files keep their symbol rows. Reference rows are stored on the file that contains the use. A full index and an incremental update of file F both walk F and record references, calls, and implementations located in F, including uses of symbols declared elsewhere, so the two paths store the same rows for the same source. When the declared symbol identities in the changed C# files change, or a C# file that had symbols is deleted, every other indexed document is walked again and its reference rows are replaced. A body-only edit leaves those other rows in place.

## One compilation per project file

A multi-targeted project loads as one Roslyn project per target framework. Symbol and reference extraction uses the highest framework for that project file (`NET10_0` outranks `NET8_0`, which outranks `netstandard` and `net48`). The workspace still loads every target. Compile errors are checked on the projects that are indexed.

## Rows are unique, and old databases rebuild

`refs` has a unique index on `(symbol_fqname, file_id, line, kind)`. `symbols` has a unique index on file, name, fqname, kind, signature, start line, end line, and container. Inserts are `INSERT OR IGNORE`, so a second copy of the same row is dropped. `meta.schema_version` is `2`. Opening a database with any other version, including a database written before versions were stored, drops the tables and recreates them. The next command reindexes from scratch.

## When C# falls back to tree-sitter

The Roslyn feeder returns failure, and the indexer retries those files with tree-sitter, when any of these happen:

- `--solution` points at a missing file, or the repo root has more than one solution file, or it has no solution file and more than one `.csproj`, or neither a root project nor a `.sln`/`.slnx` within three directories under the root is found.
- `MSBuildWorkspace` fails while opening the project.
- Any indexed project has a compile error. One error aborts the Roslyn pass for that run. Target frameworks that were not selected are not checked.

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

Resolution tries an exact `fqname` match first. If that hits, later lookups are skipped, so a fully-qualified name is not also treated as some other symbol's short name.

Otherwise the candidates are the union of an exact `name` match and a qualified-name match. Tree-sitter rows store a null `fqname`, and the key falls back to the bare name, so every definition of that name is one candidate and the qualified tier does not apply to them. For a Roslyn `fqname`, the qualified name is that string with the leading return type removed. The return type is the text before the first space that is outside parentheses and angle brackets, so a tuple or generic return type stays intact and is dropped as a whole. The query may omit the parameter list, or include it. Each parameter type may be the stored type, a dotted suffix of it (`Decimal` for `System.Decimal`), or a C# keyword alias (`decimal`, `int`, `string`, and the other built-in aliases), including on type arguments, arrays, and nullables. A trailing parameter name (`decimal amount`) and a leading `ref` / `out` / `in` / `params` are ignored.

The query's name has to match on a dotted-suffix boundary. `IPaymentProcessor.Charge` matches `SampleLib.IPaymentProcessor.Charge`, and `SampleLib.IPaymentProcessor.Charge` matches the same method. `Processor.Charge` does not match `PaymentProcessor.Charge`, because the character before the suffix is not `.`. `PaymentProcessor.Charge(decimal)` matches `System.Decimal SampleLib.PaymentProcessor.Charge(System.Decimal)`. A plain name such as `Charge` stays on the exact-name tier, so constructors (stored under the name `.ctor`) are not pulled in unless the query itself has a `.`, a parameter list, or a type-argument list. `CheckoutService.CheckoutService` matches the constructor. Omitting parameters matches every overload; `Foo()` matches only the parameterless method.

More than one distinct key is printed and the process exits 2. Each printed candidate is the stored key (the full `fqname` when Roslyn wrote one), so it can be pasted back into `refs` or `callers`. Guessing would attach the reference list to the wrong symbol. The qualified form is computed when the command runs. There is no `qualified_name` column; on the Humanizer bench database (about 2,700 symbols) an indexed `name =` lookup plus the in-memory suffix check is about a millisecond, against about 2 ms to read every key.

When nothing matches, stderr gets one line of the form `symdex: no symbol matched '<query>'. Try symdex find <last segment>.` and the process exits 0. The last segment is the identifier after the final dot, with a parameter list and type arguments removed. The printed line wraps the `symdex find` suggestion in backticks.

`callers` then keeps reference rows whose kind is `call` or `reference`. `refs` keeps every kind stored for that key, including `implementation`: Roslyn interface implementations, and TypeScript `extends` / `implements`.

## `find` uses both LIKE and FTS

`find` unions a `LIKE '%query%' ESCAPE '\'` match on `name` and `fqname` with an FTS5 prefix-phrase match on `name`, `fqname`, and `doc`. `\`, `%`, and `_` in the query are escaped, so `find _isoDuration` does not treat `_` as "one character" and `find a%b` does not treat `%` as "any sequence". The substring match is what makes `helper` return `typed_helper`. The full-text index is what makes `payments` return a symbol whose doc comment contains that word. The FTS query is a quoted phrase with a trailing `*`. A quote in the query is doubled, so it is not a string delimiter. `*`, `-`, `:`, and parentheses are FTS operators, and the tokenizer also drops `%`, `_`, and `\`, which would turn `_processor` into a prefix search for `processor` and `a%b` into the phrase `a` `b`. A query containing any of those characters is answered by the LIKE clause only. A symbol that matches both the LIKE clause and the FTS clause is one row; the two sides are `OR`ed inside one `SELECT`, not `UNION ALL`. Doc comments are not part of the LIKE clause.

## `.js` and `.jsx` use the TypeScript grammars

The vendored grammars are tree-sitter-c-sharp, tree-sitter-typescript (typescript and tsx), and tree-sitter-python. There is no tree-sitter-javascript grammar in the tree. `.ts` and `.js` are parsed with the typescript grammar. `.tsx` and `.jsx` are parsed with the tsx grammar. Both grammars accept the JavaScript those extensions contain. The typescript and tsx grammars share `queries/typescript.scm`. JSX tag names are `identifier` nodes, so that query does not mention `jsx_*` types. Those node types exist only in the tsx grammar, and a pattern that names one fails to compile for `.ts` and `.js`.

## SQLite pooling is off

The connection string sets `Pooling=False`. Microsoft.Data.Sqlite pools connections by default and can keep the file handle open after `Dispose`. Callers need to delete `.index/` immediately after closing the database (`rebuild`, and the temp copies the tests make).

## Query pattern order is positional

Tree-sitter matches are classified by `pattern_index`, not by capture name. The order of patterns in `queries/csharp.scm`, `queries/typescript.scm`, and `queries/python.scm` has to match `CSharpPatternKinds`, `TypeScriptPatternKinds`, and `PythonPatternKinds` in `GrammarRegistry.cs`.
