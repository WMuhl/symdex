# Vendored grammars

The tree-sitter runtime and language grammars under this directory are copied in, not
submodules. `scripts/vendor-grammar.ps1` (and `scripts/vendor-grammar.sh`) re-download a
tag and copy the same file set. After running it, update this file with the tag, commit,
and file list the script prints.

The runtime in `core/` accepts grammar ABI versions 13 through 15
(`TREE_SITTER_LANGUAGE_VERSION` 15, `TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION` 13 in
`core/lib/include/tree_sitter/api.h`). Do not vendor a grammar whose `LANGUAGE_VERSION`
falls outside that range without upgrading the core.

## tree-sitter core

| | |
|---|---|
| Upstream | https://github.com/tree-sitter/tree-sitter |
| Version | No release tag. In-tree `Cargo.toml` `version` is `0.27.0`, but this snapshot is 94 commits before tag `v0.27.0` and is not that release. It is also not on the `v0.26.13` line (`v0.26.13...this commit` diverges). |
| Commit | `86e1fedfafdd15b8d04d7ffbaa7516193a1d1ed1` (2026-07-25) |
| License | MIT (`LICENSE`) |
| ABI | 15, minimum compatible 13 |
| Confidence | High. All 45 vendored files are byte-identical to that commit. |

Files copied:

- `LICENSE`
- `lib/include/tree_sitter/api.h`
- `lib/src/**` except `lib/src/unicode/LICENSE`, `lib/src/unicode/README.md`, and `lib/src/unicode/ICU_SHA`

Bindings, the web package, and other `lib/` metadata are not copied.

## tree-sitter-c-sharp

| | |
|---|---|
| Upstream | https://github.com/tree-sitter/tree-sitter-c-sharp |
| Version | No release tag. Nearest tag `v0.23.5` does **not** match (`STATE_COUNT` 8053 there, 8495 here). |
| Commit | Byte-identical from `f1c7c1ee21f56a33412c12efb8abe0c483f05f0d` (2026-06-02, "fix: parse `(IDENT) && EXPR` as logical-AND, not cast") through master `8c0abe0b84a3681d3e6852ce2e2a1eecb4f731c5` (2026-09-29). Later commits in that range do not touch these files. |
| License | MIT (`LICENSE`, copyright 2014-2023) |
| ABI | `LANGUAGE_VERSION` 15 |
| Confidence | High for the commit range. The version tag can only be described as post-`v0.23.5`, unreleased. |

Files copied:

- `LICENSE`
- `src/parser.c`
- `src/scanner.c`
- `src/node-types.json`
- `src/tree_sitter/parser.h`
- `src/tree_sitter/array.h`
- `src/tree_sitter/alloc.h`

## tree-sitter-typescript

| | |
|---|---|
| Upstream | https://github.com/tree-sitter/tree-sitter-typescript |
| Version | `v0.23.2` |
| Commit | `f975a621f4e7f532fe322e13c4f79495e0a7b2e7` |
| License | MIT (`LICENSE`) |
| ABI | `LANGUAGE_VERSION` 14 (typescript and tsx) |
| Confidence | High. Vendored files are byte-identical to tag `v0.23.2`. They are also byte-identical to tag `v0.23.1` (`61e2a7730c8163ca4109f393a6285c9dc1721016`) and to master `75b3874edb2dc714fb1fd77a32013d0f8699989f` (2025-01-30); `v0.23.0` does not match. |

Files copied:

- `LICENSE`
- `common/scanner.h`
- `typescript/src/parser.c`, `scanner.c`, `node-types.json`, `tree_sitter/parser.h`, `array.h`, `alloc.h`
- `tsx/src/parser.c`, `scanner.c`, `node-types.json`, `tree_sitter/parser.h`, `array.h`, `alloc.h`

## tree-sitter-python

| | |
|---|---|
| Upstream | https://github.com/tree-sitter/tree-sitter-python |
| Version | `v0.25.0` |
| Commit | `293fdc02038ee2bf0e2e206711b69c90ac0d413f` (2025-09-11) |
| License | MIT (`LICENSE`, copyright 2016 Max Brunsfeld) |
| ABI | `LANGUAGE_VERSION` 15. The generated header says tree-sitter CLI v0.25.9. Within the vendored core's 13..15 range. |
| Confidence | High. Copied from the `v0.25.0` tag archive. |

Files copied:

- `LICENSE`
- `src/parser.c`
- `src/scanner.c`
- `src/node-types.json`
- `src/tree_sitter/parser.h`
- `src/tree_sitter/array.h`
- `src/tree_sitter/alloc.h`
