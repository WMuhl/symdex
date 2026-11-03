# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Python indexing for `.py` and `.pyi` through the vendored tree-sitter-python grammar (v0.25.0), including cross-file call sites matched by name.
- `grammars/GRAMMARS.md` and `scripts/vendor-grammar.ps1` / `vendor-grammar.sh`, which re-copy a grammar tag and print the commit and file list to record.
- `symdex mcp`, an MCP server on stdio, with client notes in `docs/mcp.md`. The server reports its assembly version. Tool calls refresh the index before answering.
- Packaging as a .NET global tool, package id `symdex`, with the tree-sitter library under `runtimes/<rid>/native/` for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.
- Release assets for those RIDs: a framework-dependent `symdex-skill-<rid>.zip` (publish output plus the `skill/` folder) and the `.nupkg`. The workflow pushes the package to nuget.org when `NUGET_API_KEY` is set.
- `skill/scripts/ensure-index.ps1` for Windows, beside the existing shell script.
- CI on Linux, Windows, and macOS, a package smoke job, and Dependabot for NuGet and GitHub Actions.
- Tests for `find`, `--limit`, and `--json`; repo-scanner ignore rules; incremental reindex; the Roslyn-to-tree-sitter fallback; the Python fixture; and the MCP server.

### Changed

- Target framework is `net10.0`. `global.json` pins the 10.0.100 SDK with `rollForward: latestFeature`. Release 1.0.0 targeted `net8.0` and SDK 8.0.423.
- CLI parsing uses System.CommandLine 2.0.14. Release 1.0.0 used the 2.0 beta. Flags, text output, and the ambiguous-symbol exit code are unchanged.
- Release zips are framework-dependent and are not single-file. Release 1.0.0 published self-contained single-file zips for `linux-x64`, `osx-arm64`, and `win-x64`. Single-file publish leaves `Assembly.Location` empty, and the Roslyn feeder then cannot locate the SDK. See [docs/design-decisions.md](docs/design-decisions.md).

### Fixed

- Roslyn opens a project with the SDK `dotnet --version` resolves in that repository. It previously registered the host process's MSBuild, so a repo restored by another SDK compiled with no reference assemblies and fell back to tree-sitter.
- `refs` and `callers` prefer an exact fully-qualified name. A query that matches an `fqname` no longer also matches a different symbol that has that string as its short name.
- `refs` returns no rows when the symbol key is missing. Binding a null key used to throw from Microsoft.Data.Sqlite.
- The Windows native build waits on `cl.exe` only. Waiting on the process tree stalled the build while MSVC's `vctip.exe` telemetry child was still running.

## [1.0.0] - 2026-07-25

### Added

- Per-repo SQLite index (`.index/code.db`) with an inline freshness check on every command.
- Roslyn feeder for C# (symbols, references, implementations) and a tree-sitter feeder for TypeScript, JavaScript, and C# when Roslyn cannot load the project.
- CLI commands `outline`, `find`, `refs`, `callers`, and `rebuild`, including `--repo`, `--json`, `--limit`, and `--solution`.
- Agent skill under `skill/`.
- Sample-repo tests, a build/test workflow, and a tag-triggered release workflow that published self-contained single-file zips.
- MIT license.

[Unreleased]: https://github.com/WMuhl/symdex/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/WMuhl/symdex/releases/tag/v1.0.0
