# symdex

[![CI](https://github.com/WMuhl/symdex/actions/workflows/ci.yml/badge.svg)](https://github.com/WMuhl/symdex/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/WMuhl/symdex)](https://github.com/WMuhl/symdex/releases/latest)
[![License: MIT](https://img.shields.io/github/license/WMuhl/symdex)](LICENSE)

symdex is a local code index for coding agents. It stores symbols and reference sites for one repository in a SQLite file, and answers where a name is defined or used. The agent can then open a line range instead of grepping and reading whole files, which is less context to put in a prompt. The index stays in `.index/code.db` on the machine that has the checkout. symdex has no telemetry and does not upload source or the index.

## Quick start

The package id is `symdex`. It is not on nuget.org yet: [the release workflow](.github/workflows/release.yml) pushes there only when the `NUGET_API_KEY` secret is set.

**Global tool, once that push has happened:**

```bash
dotnet tool install -g symdex
```

**From a downloaded `.nupkg`.** Releases attach `symdex.<version>.nupkg`. Put the file in a directory and install from that directory (this form works before the package is on nuget.org):

```bash
dotnet tool install -g symdex --add-source <directory-containing-the-nupkg> --version <version>
```

**From a release zip.** The same workflow builds `symdex-skill-<rid>.zip` for `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, and `win-x64`. Each zip is a framework-dependent publish (not a single file) plus the `skill/` folder, with the tree-sitter library next to the executable. Unzip it and run `symdex` (`symdex.exe` on Windows). The .NET 10 runtime is enough to start that binary. Semantic C# indexing still needs the SDK in the table below.

The 1.0.0 release assets are older: self-contained single-file zips for `linux-x64`, `osx-arm64`, and `win-x64` only, and no `.nupkg`.

**From this checkout** (the `--` keeps `dotnet run` from treating the symdex arguments as its own):

```bash
dotnet run --project src/Symdex.Cli -- find <name> --repo <path-to-repo>
```

Building needs the .NET 10 SDK and a C compiler. See [Building](#building).

Then, in the repository you want to index:

```bash
symdex find <name>
```

The first command creates `.index/code.db` and prints `reindexed N files` on stderr. Later commands reuse the file and reindex only what changed. Add `.index/` to that repository's `.gitignore`.

C# results that name a specific method or an implementing type need a project Roslyn can open. See the table below.

## Use it with your agent

### MCP

`symdex mcp` is an MCP server on stdio. Client setup for Cursor, Claude Code, VS Code, and Codex is in [docs/mcp.md](docs/mcp.md).

Claude Code:

```bash
claude mcp add symdex -- symdex mcp
```

Cursor, in `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "symdex": {
      "command": "symdex",
      "args": ["mcp"]
    }
  }
}
```

### Skill

[skill/SKILL.md](skill/SKILL.md) tells an agent to call the CLI before grepping. `skill/scripts/ensure-index.sh` and `skill/scripts/ensure-index.ps1` run one `find` so the index exists. Release zips place `skill/` next to the `symdex` binary, which is the first place those scripts look.

## Supported languages

| Language | Extensions | Engine | What `refs` / `callers` mean | Requirements |
| --- | --- | --- | --- | --- |
| C# | `.cs` | Roslyn (semantic). Tree-sitter if the project cannot be opened or has compile errors. | Roslyn binds the symbol. `refs` lists references and implementations. `callers` lists calls and other references to that symbol. Fully-qualified names look like `SampleLib.IPaymentProcessor` and `System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)`. A short name that matches more than one of those exits 2. The tree-sitter fallback stores no fully-qualified name, and its C# query records `using` directives, not call sites. | One `.sln` at the repo root, or a single `.csproj` there if there is no solution. Otherwise pass `--solution`. The .NET SDK that `dotnet --version` prints in that repo must be installed. A project with package references has to be restored first (`dotnet restore`); an unrestored `PackageReference` fails the workspace load and that run uses tree-sitter. An SDK project with no package references can load without restore. |
| TypeScript | `.ts`, `.tsx` | tree-sitter. `.ts` uses the typescript grammar. `.tsx` uses the tsx grammar. | Name match, including across files: calls (`foo()`, `obj.foo()`, `new Foo()`, `obj?.foo()`), imports, type names, JSX element names, and other identifiers. `extends` and `implements` are kind `implementation`. Two definitions of the same name share one key. In the sample repo, `refs helper` is the import on `ts/math.ts:1` and the call inside `add`. | The tree-sitter library shipped with the tool or built with symdex. |
| JavaScript | `.js`, `.jsx` | tree-sitter. `.js` uses the typescript grammar. `.jsx` uses the tsx grammar. A separate JavaScript grammar is not vendored. | Same as TypeScript. | Same as TypeScript. |
| Python | `.py`, `.pyi` | tree-sitter | Name match, including across files: calls, imports, and other identifiers with that text. Two definitions of the same name share one key. Module-level assignments are stored as kind `export` (the sample's `PI`). | Same as TypeScript. |

Any compile error in a C# project, or a workspace that does not load, indexes the changed `.cs` files with tree-sitter for that run and writes a `symdex:` warning on stderr.

## Commands

`outline`, `find`, `refs`, and `callers` take `--repo <path>` (default: the current directory), `--json` (one JSON object per line), `--limit <n>` (default 50, clamped to 1 through 200), and `--solution <path>` (passed through to the C# feeder). `rebuild` takes `--repo`, `--json`, and `--solution`. `symdex --help` lists them. `refs` and `callers` exit 2 when the name is ambiguous. An unknown name prints nothing and exits 0.

The examples are real output from `tests/fixtures/sample-repo` (C# and TypeScript, restored with `dotnet restore`) and `tests/fixtures/python-sample`. Paths in the output are relative to that fixture. Stderr is omitted; the first command also prints `reindexed N files` there.

`outline` lists the symbols declared in one file. Signatures do not include bodies.

```bash
symdex outline csharp/SampleLib/PaymentProcessor.cs
```

```text
class PaymentProcessor class PaymentProcessor :4-11
method Charge public decimal Charge(decimal amount) :7-10
```

`find` searches names and fully-qualified names by substring, and the same fields plus doc comments through a full-text prefix. `--kind` keeps one kind: `class`, `interface`, `struct`, `enum`, `method`, `property`, `field`, `function`, `export`, or `type`.

```bash
symdex find IPaymentProcessor --kind interface
```

```text
interface SampleLib.IPaymentProcessor interface IPaymentProcessor csharp/SampleLib/IPaymentProcessor.cs:4-8
```

`symdex find payments` returns that same interface, because its summary is "Processes payments." Without `--kind`, `find IPaymentProcessor` also returns the field and constructor on `CheckoutService` whose fully-qualified names contain that text, and `IPaymentProcessor.Charge`.

`refs` lists reference sites for one symbol.

```bash
symdex refs IPaymentProcessor
```

```text
SampleLib.IPaymentProcessor
reference csharp/SampleLib/CheckoutService.cs:6
reference csharp/SampleLib/CheckoutService.cs:9
reference csharp/SampleLib/PaymentProcessor.cs:4
implementation csharp/SampleLib/PaymentProcessor.cs:4
```

`callers` is the same lookup limited to kinds `call` and `reference`. Pass the fully-qualified name when the short name matches more than one symbol. The short name `Charge` exits 2 and prints both candidates on stderr:

```text
ambiguous symbol 'Charge', candidates:
  System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)
  System.Decimal SampleLib.PaymentProcessor.Charge(System.Decimal)
```

```bash
symdex callers "System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)"
```

```text
System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)
call csharp/SampleLib/CheckoutService.cs:17
```

With `--json`, that `refs` query is:

```text
{"symbol":"SampleLib.IPaymentProcessor"}
{"kind":"reference","filePath":"csharp/SampleLib/CheckoutService.cs","line":6}
{"kind":"reference","filePath":"csharp/SampleLib/CheckoutService.cs","line":9}
{"kind":"reference","filePath":"csharp/SampleLib/PaymentProcessor.cs","line":4}
{"kind":"implementation","filePath":"csharp/SampleLib/PaymentProcessor.cs","line":4}
```

An ambiguous `--json` query prints one object on stdout (`ambiguous`, `query`, `candidates`) and still exits 2. Past `--limit`, a text query adds a line `... +K more (narrow the query)`; JSON adds `{"truncated":true,"more":K}`.

In the Python fixture, `callers` follows the name across files. The import is a `reference`; the calls are `call`:

```bash
symdex callers helper
```

```text
helper
reference app.py:1
call app.py:5
call calc.py:16
```

In the TypeScript sample the same command is the import and the call inside `add`:

```bash
symdex callers helper
```

```text
helper
reference ts/math.ts:1
call ts/math.ts:5
```

`rebuild` deletes the database and indexes every candidate file again. On the C# sample that prints `rebuilt index: 5 files indexed`. Ordinary queries do not need it.

`mcp` serves the same queries over MCP. It takes `--repo` and `--solution` and does not take a symbol on the command line. See [docs/mcp.md](docs/mcp.md).

## How it works

Each repository gets its own SQLite database at `.index/code.db` (tables `files`, `symbols`, `refs`, and an FTS index `symbols_fts`). Every command hashes the files it would index, compares the hashes to the `files` table, and reindexes only added, changed, and deleted files before it answers.

Discovery uses `git ls-files` when the directory is inside a git work tree, so gitignored files are left out. Directory names such as `bin`, `obj`, `node_modules`, `.git`, and `.index` are skipped either way, and so are files larger than 1 MB.

C# is offered to Roslyn first. Roslyn loads the whole solution, which is what cross-file references need, and writes rows only for the files that changed. If that load fails, or any project in the solution has a compile error, those `.cs` files are parsed with tree-sitter instead. TypeScript, JavaScript, and Python are tree-sitter only. The grammars are vendored; provenance is in [src/Symdex.TreeSitter/grammars/GRAMMARS.md](src/Symdex.TreeSitter/grammars/GRAMMARS.md).

If several `.sln` files sit at the repo root, pass `--solution`. The same applies when there is no solution and more than one `.csproj` at the root.

## Compared with other indexes

**universal-ctags** writes a tags file of definitions for many languages, with no NuGet restore and no language server. symdex covers C#, TypeScript, JavaScript, and Python. On a restored C# project it can name the implementing type and the call site; ctags stays the wider list of definitions.

**LSP-based agent tools** (for example Serena) ask a language server, so they can use that compiler's model and often edit or hover as well. They need the server installed and the project loaded for the session. symdex answers `outline`, `find`, `refs`, and `callers` from a SQLite file. The process exits after a CLI query, or stays up only while an MCP client holds `symdex mcp`.

**Sourcegraph and SCIP indexers** are built for search across many repositories, usually as a service. SCIP is an interchange format for precise facts. symdex stores one repo in `.index/code.db` on the local disk. It does not read or write SCIP.

**ripgrep** (and grep) finds text in every language, including strings and comments. symdex returns symbol records for the extensions in the table. A string literal, or a language symdex does not parse, is still a grep query.

## Benchmarks

On 22 scripted questions across Humanizer (C#, Roslyn), zod (TypeScript), and requests (Python),
symdex's output plus the line spans it reports came to about 2% of the bytes of a grep-then-read-the-files
baseline (49,769 vs 2,357,421). The cost is recall: 0.79 versus grep's 1.00. Four questions came back
empty. Three of those were TypeScript `refs` and `callers` questions; that run did not index calls or identifier references (it does now). Outlines of small files cost more than
just reading them. Where symdex did return locations, mean precision was 0.83 versus grep's 0.35. The
"agent" is a fixed rule, not a model, and tokens are estimated as bytes/4. Methodology, per-question
results, latencies, and known losses are in [bench/README.md](bench/README.md).

## Building

Install the .NET 10 SDK. `global.json` requests 10.0.100 and rolls forward to the latest 10.0 feature band. The tree-sitter library is compiled with MSVC on Windows (Visual Studio Build Tools, including the C++ tools) and with `cc`, `clang`, or `gcc` on Linux and macOS. If no compiler is found, the build still succeeds and tree-sitter languages come back empty. [CONTRIBUTING.md](CONTRIBUTING.md) has the per-OS setup.

```bash
dotnet build symdex.sln
dotnet test symdex.sln
```

## Design decisions

The choices that look accidental, including which SDK Roslyn loads and why the published tool is not a single file, are written up in [docs/design-decisions.md](docs/design-decisions.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Behavior changes are described in [CHANGELOG.md](CHANGELOG.md). Vulnerability reports go through [SECURITY.md](SECURITY.md). The code of conduct is [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## License

MIT. See [LICENSE](LICENSE). The vendored tree-sitter runtime and grammars are MIT; the versions are recorded in [src/Symdex.TreeSitter/grammars/GRAMMARS.md](src/Symdex.TreeSitter/grammars/GRAMMARS.md).
