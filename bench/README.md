# Symdex benchmark

On 22 questions across three pinned repos, symdex's JSON plus the source spans it pointed at was **47,660 bytes**. The grep-or-read baseline was **2,357,421 bytes**. Ratio **0.0202** (about 11,900 vs 589,000 tokens at bytes/4). Mean precision where symdex returned locations was **0.853** (21 questions); mean recall across all 22 was **0.955**. The baseline's mean precision on grep questions was **0.364**, and its recall was **1.00**.

The byte ratio is almost the same as the [2026-11-02](results/2026-11-02.json) run (0.0211). Recall is not: that run was 0.786, with four empty answers. This run has one empty answer. Two small-file outlines still cost more than reading the file. Tool calls went the other way, and further the wrong way (151 vs 89; was 126 vs 89), because the questions that used to return nothing now have spans to open.

Raw rows are in [`results/2026-11-03.json`](results/2026-11-03.json). The previous run is kept at [`results/2026-11-02.json`](results/2026-11-02.json). Questions and expected `file:line` sets are in [`tasks.json`](tasks.json).

## How to run

From the repo root, after `dotnet build symdex.sln -c Release`:

```powershell
pwsh -NoProfile -File .\bench\run-bench.ps1 -Date 2026-11-03
```

The script is PowerShell 7 (`#requires -Version 7`). It is not part of `symdex.sln`. Clones go to `$env:TEMP\symdex-bench\work` (override with `-WorkRoot`). That directory is deleted and recreated at the start of every run. `-Only <id>` runs one target. The symdex binary defaults to `src/Symdex.Cli/bin/Release/net10.0/symdex.exe`.

Humanizer is measured twice. The comparable run passes `--solution src/Humanizer/Humanizer.csproj`, same as 2026-11-02. A supplementary run then deletes `.index/`, runs `dotnet restore src/Humanizer.slnx`, and repeats the same seven questions with no `--solution`. That block is labelled `humanizer-autosln` and is not in the 22-question overall.

## Methodology

Each target is a shallow `git clone --depth 1 --branch <tag>`. Humanizer is then `git fetch --unshallow` (see below). The script checks `HEAD` against the commit in `tasks.json`. Prep commands run next (Humanizer: `dotnet restore` of the library project). `.index/` is deleted so the first query is cold.

Context bytes are UTF-8 of stdout. Stderr (`reindexed N files`, warnings) is not context. Approximate tokens are bytes/4.

### Symdex path

The measured output is `--json --limit 50` (the CLI default limit; the hard cap is 200). Predictions are the unique `file:startLine` values for `find` and `outline`, and `file:line` for `refs` and `callers`.

An agent then reads those spans, with three caps:

- identical spans are read once
- the first 20 distinct spans (`maxSpans`)
- each span is at most 12 lines from its start line (`spanCapLines`); the raw file bytes of those lines are counted, including newlines
- overlapping or adjacent spans in one file are merged before the byte count

Tool calls = 1 (the symdex invocation) + the number of merged spans. Questions with more than 20 distinct locations are scored on every location, but bytes and tool calls stop at the cap. On this run that cap binds for `outline-sessions` (31 locations), `callers-session-send` (43), and the supplementary `callers-tofullwords` (50 locations printed, 78 expected).

### Grep path

`git grep -n -I -F -- <term>` over the whole clone, tracked files, including docs and tests. Predictions are the unique `file:line` hits.

The read rule: open, in full, every distinct file among the first 5 hits, in first-appearance order (`grepK` = 5). Tool calls = 1 + files opened. Bytes = grep stdout + those file sizes.

### Outline path

The baseline is one full-file read, not grep. Recall is the fraction of expected lines that sit in that file (1.00 here, because every expected line was chosen inside the file). Precision is null, because a read does not extract locations. Those nulls are omitted from the baseline precision mean. They are included in baseline recall, which is why that mean is 1.00 even before counting grep. Every grep question in this set also had baseline recall 1.00: each expected line contains the fixed string that was grepped.

### Scoring

Expected locations answer the natural-language question. They are not "every row the tool's matcher is specified to emit." A partial-name search that returns members is a false positive if the question asked for the type. An import is a false positive if the question asked for callers. The declaration line is not a call.

Precision and recall are on the unique `file:line` sets. Precision is null when a path returns no locations and the expected set is non-empty. The published mean skips those nulls. Treating the one empty answer as precision 0 gives a strict mean of **0.815** over all 22 questions (the defined-only mean is 0.853 over 21). Both-empty would be precision 1 and recall 1; that did not occur. False positives in the JSON are capped at 25 per question; the counts `fp` / `fn` are complete.

One expected line changed since 2026-11-02. `refs` of `ZodMiniISODuration` now includes `packages/zod/src/v4/mini/iso.ts:56`. That line is `export const ZodMiniISODuration: core.$constructor<ZodMiniISODuration>`. The const name is a declaration; the type argument on the same line is a reference, and scoring is per line. The 2026-11-02 note treated the whole line as a declaration. No other expected line moved.

### Latency

- **Cold** is the first question's first invocation on a fresh clone after restore and unshallow, with `.index/` absent. It includes process startup, indexing, and that query. Clone, unshallow, and `dotnet restore` are timed separately and are not part of cold time.
- **Warm** is the median of 5 further invocations of the same question, no file changes. Median is the lower middle of the sorted sample, index `(n-1)/2`. Warm stderr did not contain `reindexed`. The time includes CLI startup and hashing every scanned file. An in-process MCP call was not measured.
- **Incremental** runs after the scored questions. The watch query is snapshotted, one newline is appended to one file (existing line numbers stay put), and the same query is timed. `watchedLinesDropped` is true when that file had locations before the touch and none after.

A `symdex:` line on the C# cold stderr means Roslyn declined and tree-sitter indexed those files, except `symdex: using '...'`, which reports an auto-discovered solution. The comparable Humanizer run had no `symdex:` line (`roslynFallback: false`). The supplementary run's only `symdex:` line is the `using` note, and `roslynFallback` is false there too.

## Machine and commits

| | |
|---|---|
| OS | Windows NT 10.0.26200.0 |
| PowerShell | 7.6.9 |
| CPU | AMD Ryzen 9 9950X3D 16-Core |
| RAM | 61.58 GB |
| `dotnet --version` | 10.0.204 (SDKs 8.0.427 and 10.0.204 also installed) |
| Worktree HEAD at measurement | `f56caef7b88457e9118d29c0f2b98796f4d33021` (`bench/harness`, fast-forwarded to the integration branch) |
| Binary | `src/Symdex.Cli/bin/Release/net10.0/symdex.exe` built from that commit |

`results/2026-11-03.json` records that commit. The results commit adds the JSON and this write-up; it does not change `src/`.

## Targets

| id | repo | tag | commit | clone |
|---|---|---|---|---|
| humanizer | Humanizr/Humanizer | v3.0.10 | `90f0cb1d7427635f38b46350bb1dc5fca2164f2d` | shallow, then unshallow |
| zod | colinhacks/zod | v4.6.7 | `b500a2ee95134f81d74da7911d7378dd2853d042` | shallow |
| requests | psf/requests | v2.32.5 | `b25c87d7cb8d6a18a37fa12442b5f883f9e41741` | shallow |

Humanizer has no `.sln` or `.csproj` at the repo root. The comparable run passes `--solution src/Humanizer/Humanizer.csproj`. A depth-1 clone still makes MSBuildWorkspace fail: Nerdbank.GitVersioning cannot compute version height, Roslyn aborts, and symdex falls back to tree-sitter. Re-checked on this machine after the fixes, against a fresh depth-1 clone of the same tag: stderr starts with `symdex: workspace load reported 1 failure(s)` and the Nerdbank shallow-clone message, then `reindexed 534 files`, and `find ByteSize --kind struct` returns `fqName` `ByteSize` (tree-sitter, not `Humanizer.ByteSize`). The scored run unshallows first. `dotnet restore src/Humanizer/Humanizer.csproj` exited 0. The project multi-targets `net10.0;net8.0;net48;netstandard2.0`. The library-only compilation does not include `Humanizer.Tests`.

Prep, not included in cold time: Humanizer clone 1189 ms, unshallow 2220 ms, restore 867 ms. Zod clone is in the JSON (`cloneMs`). Requests likewise.

## Results

What `run-bench.ps1` printed. Ratios and precision in this table are formatted to two decimal places, so 0.0112 prints as 0.01. The next table uses the JSON values.

```
target          cold_ms   index_kb  sym_bytes  base_bytes  ratio  sym_P  sym_R  base_P  base_R
humanizer          8830      3240      10218      915539   0.01  0.86  0.86  0.24  1.00
zod                9524     12332      12305      780161   0.02  0.88  1.00  0.37  1.00
requests            883      1420      25137      661721   0.04  0.83  1.00  0.45  1.00
OVERALL sym=47660 base=2357421 ratio=0.0202 P=0.8534976005906239 R=0.9545454545454546 baseP=0.3635711013868152 baseR=1 n=22
SUPPLEMENTARY (not in OVERALL)
humanizer-autosln    17232      7128      29697      915539   0.03  0.79  0.95  0.24  1.00
```

| target | cold | files | index | sym bytes | base bytes | ratio | sym P | sym R | base P | warm median |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| humanizer | 8.8 s | 534 | 3,317,760 | 10,218 | 915,539 | 0.0112 | 0.861 | 0.857 | 0.245 | 199 ms |
| zod | 9.5 s | 513 | 12,627,968 | 12,305 | 780,161 | 0.0158 | 0.875 | 1.000 | 0.373 | 196 ms |
| requests | 0.88 s | 36 | 1,454,080 | 25,137 | 661,721 | 0.0380 | 0.829 | 1.000 | 0.453 | 160 ms |
| overall | | | | 47,660 | 2,357,421 | 0.0202 | 0.853 | 0.955 | 0.364 | |
| humanizer-autosln | 17.2 s | 534 | 7,299,072 | 29,697 | 915,539 | 0.0324 | 0.791 | 0.949 | 0.245 | 203 ms |

`humanizer-autosln` repeats Humanizer's seven questions against the same baseline files, so its baseline bytes match the Humanizer row. It is not added into the overall.

### Before and after

Same 22 questions, same pins, same rules. The comparable Humanizer command is still `--solution src/Humanizer/Humanizer.csproj`.

| | 2026-11-02 | 2026-11-03 |
|---|---:|---:|
| symdex bytes | 49,769 | 47,660 |
| baseline bytes | 2,357,421 | 2,357,421 |
| context ratio | 0.0211 | 0.0202 |
| precision, defined only | 0.827 (18 questions) | 0.853 (21 questions) |
| precision, empty answers as 0 | 0.677 | 0.815 |
| recall | 0.786 | 0.955 |
| empty answers | 4 | 1 |
| tool calls (symdex / baseline) | 126 / 89 | 151 / 89 |
| Humanizer cold | 28.3 s, 33.8 MB | 8.8 s, 3.3 MB |
| zod cold | 6.1 s, 1.16 MB | 9.5 s, 12.0 MB |
| requests cold | 1.44 s, 0.95 MB | 0.88 s, 1.45 MB |
| warm median (humanizer / zod / requests) | 197 / 193 / 164 ms | 199 / 196 / 160 ms |
| Humanizer incremental | 6,373 ms, 4 watched lines dropped | 3,673 ms, lines kept |
| zod incremental | 227 ms, no refs stored | 272 ms, watched lines kept |
| requests incremental | 211 ms, lines kept | 223 ms, lines kept |

Baseline precision moved from 0.354 to 0.364 because `iso.ts:56` was added to the expected set. That line was already a grep hit, so it became a true positive for grep as well as for symdex. Symdex's byte total did not move because of that edit: the span was already being read.

Zod's index grew and its cold start slowed because calls and identifier references are stored now. Humanizer's index shrank and its cold start sped up because a multi-targeted project is indexed once, at the highest TFM, and duplicate rows are not written.

### Per question

`warm` is the median of 5. `dup` is raw JSON rows / unique lines. `calls` is symdex/baseline. A blank baseline precision is an outline (full-file read).

#### Humanizer (C#, Roslyn, library project)

| question | verb | warm | raw/uniq | dup | sym P | sym R | base P | ratio | sym B | base B | calls |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| locate-bytesize | find | 198 | 1/1 | 1 | 1.00 | 1.00 | 0.001 | 0.0031 | 593 | 191,361 | 2/6 |
| locate-default-formatter | find | 201 | 1/1 | 1 | 1.00 | 1.00 | 0.013 | 0.0082 | 543 | 66,298 | 2/6 |
| partial-byterate | find | 201 | 6/6 | 1 | 0.17 | 1.00 | 0.053 | 0.0123 | 2,498 | 203,463 | 5/6 |
| outline-truncator | outline | 199 | 6/6 | 1 | 1.00 | 1.00 | | 1.4314 | 1,566 | 1,094 | 5/1 |
| outline-iformatter | outline | 202 | 9/9 | 1 | 1.00 | 1.00 | | 0.8466 | 1,783 | 2,106 | 7/1 |
| refs-itruncator | refs | 197 | 19/14 | 1.36 | 1.00 | 1.00 | 0.280 | 0.0435 | 3,137 | 72,058 | 15/6 |
| callers-tofullwords | callers | 195 | 0/0 | | | 0.00 | 0.876 | 0.0003 | 98 | 379,159 | 1/6 |

`partial-byterate` false positives are members of `ByteRate` and `ByteSizeExtensions.Per` (fqname contains `ByteRa`). `refs-itruncator` now returns all 14 expected lines and is not truncated. The five implementing-class lines are stored twice, once as `implementation` and once as `reference`, which is the 1.36 factor. `callers-tofullwords` is still empty: all 78 call sites are under `tests/`, and this compilation is the library project only.

#### Humanizer, no `--solution` (supplementary)

Cold stderr: `symdex: using 'src/Humanizer.slnx' (no solution at the repo root; pass --solution to override).` then `reindexed 534 files`. Restore of `src/Humanizer.slnx` exited 0 in 1,033 ms. Roslyn was used (`roslynFallback: false`).

| question | warm | raw/uniq | sym P | sym R | ratio | sym B | calls | note |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| locate-bytesize | 205 | 1/1 | 1.00 | 1.00 | 0.0031 | 593 | 2 | same as the library run |
| locate-default-formatter | 203 | 2/2 | 0.50 | 1.00 | 0.0163 | 1,079 | 3 | also `DefaultFormatterTests.cs:3` |
| partial-byterate | 206 | 26/26 | 0.04 | 1.00 | 0.0733 | 14,907 | 16 | test names contain `ByteRa` |
| outline-truncator | 196 | 6/6 | 1.00 | 1.00 | 1.4314 | 1,566 | 5 | same loss as the library run |
| outline-iformatter | 204 | 9/9 | 1.00 | 1.00 | 0.8466 | 1,783 | 7 | |
| refs-itruncator | 203 | 19/14 | 1.00 | 1.00 | 0.0435 | 3,137 | 15 | same 14 lines, dup 1.36 |
| callers-tofullwords | 197 | 50/50 | 1.00 | 0.64 | 0.0175 | 6,632 | 21 | truncated at `--limit 50`; 28 of 78 sites missing |

Warm medians for this block are 196–206 ms (target median 203 ms). Incremental: append a newline to `TruncateExtensions.cs`, `refs ITruncator` took 6,373 ms, `reindexed 1 files`, and lines 38, 56, 96, 117 were still there.

The 50 `ToFullWords` rows that fit are all expected call sites (precision 1). Recall is 50/78 because the page stops at the default limit. The missed lines continue through the localisation test files (`is`, `lb`, `ru-RU`, and so on).

#### Zod (TypeScript, tree-sitter)

| question | verb | warm | raw/uniq | sym P | sym R | base P | ratio | sym B | base B | calls |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| locate-iso-duration-fn | find | 197 | 1/1 | 1.00 | 1.00 | 0.250 | 0.0038 | 636 | 169,262 | 2/5 |
| locate-mini-iso-duration-type | find | 196 | 1/1 | 1.00 | 1.00 | 0.167 | 0.0893 | 366 | 4,098 | 2/3 |
| partial-mini-iso | find | 194 | 8/8 | 1.00 | 1.00 | 0.320 | 0.1347 | 3,581 | 26,578 | 5/4 |
| outline-mini-iso | outline | 193 | 12/12 | 1.00 | 1.00 | | 1.9128 | 4,409 | 2,305 | 9/1 |
| refs-mini-iso-duration | refs | 197 | 4/4 | 1.00 | 1.00 | 0.667 | 0.1530 | 627 | 4,098 | 4/3 |
| callers-iso-duration-helper | callers | 196 | 3/3 | 1.00 | 1.00 | 0.750 | 0.0026 | 440 | 169,262 | 4/5 |
| callers-mini-duration | callers | 201 | 16/16 | 0.13 | 1.00 | 0.083 | 0.0056 | 2,246 | 404,558 | 16/6 |

`find _isoDuration` returns the one function at `packages/zod/src/v4/core/api.ts:598`. On 2026-11-02 it returned 11 symbols, precision 0.09, because `_` was a SQL `LIKE` wildcard. `callers _isoDuration` returns the three expected call sites. `callers duration` returns those two mini-ISO test calls and 14 other `duration(` sites (classic, v3, JSON-schema tests). Recall of the two expected lines is 1; precision is 0.125. Grep of `duration(` is 0.083 on the same expected set.

#### Requests (Python, tree-sitter)

Unchanged in score and in byte cost from 2026-11-02. Same eight questions, same precision and recall, same 25,137 symdex bytes.

| question | verb | warm | raw/uniq | sym P | sym R | base P | ratio | sym B | base B | calls |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| locate-prepared-request | find | 162 | 1/1 | 1.00 | 1.00 | 0.017 | 0.0045 | 674 | 151,066 | 2/6 |
| outline-sessions | outline | 157 | 31/31 | 1.00 | 1.00 | | 0.3954 | 12,391 | 31,334 | 20/1 |
| outline-api | outline | 158 | 8/8 | 1.00 | 1.00 | | 0.7511 | 4,962 | 6,606 | 9/1 |
| callers-merge-setting | callers | 160 | 8/8 | 1.00 | 1.00 | 0.889 | 0.0306 | 983 | 32,099 | 5/2 |
| refs-requote-uri | refs | 163 | 7/7 | 1.00 | 1.00 | 0.778 | 0.0057 | 766 | 133,894 | 8/5 |
| partial-requote | find | 155 | 2/2 | 0.50 | 1.00 | 0.111 | 0.0115 | 1,542 | 133,894 | 3/5 |
| callers-dispatch-hook | callers | 162 | 3/3 | 0.67 | 1.00 | 0.500 | 0.0128 | 420 | 32,900 | 4/4 |
| callers-session-send | callers | 160 | 43/43 | 0.47 | 1.00 | 0.426 | 0.0243 | 3,399 | 139,928 | 21/6 |

`partial-requote` also returns `test_requote_uri_with_unquoted_percents`. `callers-dispatch-hook` counts the import at `sessions.py:30`. `callers-session-send` returns every `.send` the tree-sitter query sees (`adapter.send`, `connection.send`, `sock.send`): 23 false positives, recall 1 of the 20 `Session.send` sites. Span reads stop at 20 of 43 locations.

### Where symdex lost

- **Bytes.** `outline-truncator` 1,566 vs 1,094 (ratio 1.43, 5 calls vs 1). `outline-mini-iso` 4,409 vs 2,305 (ratio 1.91, 9 vs 1). Same files as last time: `Truncator.cs` is 32 lines, `mini/iso.ts` is 66.
- **Tool calls.** 151 vs 89. The increase from 126 is the zod `refs` and `callers` questions, which used to be a single header and now open spans. `outline-sessions` is still cheaper in bytes (ratio 0.40) at 20 calls against one read.
- **The remaining empty answer.** `callers ToFullWords` with `--solution src/Humanizer/Humanizer.csproj` is 98 bytes and recall 0. Grep finds all 78 test call sites (precision 0.88). Loading `src/Humanizer.slnx` instead returns 50 of those 78, all correct, and stops because `--limit 50` is truncated (recall 0.64, 6,632 bytes).
- **Partial names and common names.** `find ByteRa` precision 0.17 (grep 0.05) on the library project, and 0.04 once the tests are in the index. `find requote` precision 0.50 (grep 0.11). `callers send` precision 0.47 (grep 0.43). `callers duration` precision 0.13 (grep 0.08) with recall 1. `callers dispatch_hook` precision 0.67 because the import is a caller. All of these are smaller than grep and still include the wrong lines.
- **Two kinds on one line.** `refs ITruncator` unique-line precision and recall are both 1, and the page is not truncated. Five of the 14 lines are emitted twice (`implementation` and `reference`). Duplicate factor 1.36. On 2026-11-02 the same query was 50 rows, 4 unique lines, factor 12.5, truncated, recall 0.29.

Locate-a-type with `--kind` is still the clean win: `ByteSize`, `DefaultFormatter` (library project), `ZodMiniISODuration`, `PreparedRequest`, and now `_isoDuration`, all precision 1 and recall 1.

### Latency

Warm medians sit between 155 ms and 202 ms. By verb, the lower-middle of those medians: find 197 ms (8 questions, 155–201), outline 193 ms (5, 157–202), refs 197 ms (3, 163–197), callers 162 ms (6, 160–201). Process startup is inside every sample. These are within a few tens of milliseconds of the 2026-11-02 warm numbers.

| | cold | index | incremental |
|---|---:|---:|---|
| humanizer | 8,830 ms | 3.3 MB, 534 files | 3,673 ms, `reindexed 1 files`, watched lines kept |
| zod | 9,524 ms | 12.0 MB, 513 files | 272 ms, `reindexed 1 files`, watched lines kept |
| requests | 883 ms | 1.45 MB, 36 files | 223 ms, `reindexed 1 files`, all 8 call lines kept |
| humanizer-autosln | 17,232 ms | 7.3 MB, 534 files | 6,373 ms, `reindexed 1 files`, watched lines kept |

The Humanizer watch file is `src/Humanizer/TruncateExtensions.cs`. Before and after the newline: lines 38, 56, 96, 117. On 2026-11-02 those four lines disappeared and the reindex took 6,373 ms. The zod watch file is `packages/zod/src/v4/mini/iso.ts`; lines 56, 64, and 65 survived. The requests watch file is `src/requests/sessions.py`; lines 103, 490, 493, 494, 774, 775, 776, 777 survived.

A one-file Roslyn reindex is still seconds (3.7 s for the library project, 6.4 s for the solution that includes tests). Tree-sitter reindex of one file is about 220–270 ms.

## Bugs from the 2026-11-02 report

Re-checked against this run. Nothing in `src/` was changed for the measurement.

### Fixed

**Multi-target ref explosion.** Humanizer still targets `net10.0;net8.0;net48;netstandard2.0`. `refs ITruncator` at `--limit 50` is 19 rows, 14 unique lines, not truncated, recall 1. Index of that project is 3.3 MB and the cold query is 8.8 s (was 33.8 MB and 28.3 s). The five doubled lines are `implementation` plus `reference` on the same class declaration, confirmed by grouping the JSON `kind` field. That is not the old per-TFM copy of every row.

**Incremental drop of cross-file refs.** Appending a newline to `src/Humanizer/TruncateExtensions.cs` and running `refs ITruncator` prints `reindexed 1 files` in 3,673 ms and still returns lines 38, 56, 96, and 117. The same check on the auto-discovered solution kept those lines in 6,373 ms.

**TypeScript calls and identifier references.** `refs ZodMiniISODuration`, `callers _isoDuration`, and `callers duration` on zod `v4.6.7` return locations. The first two match the expected set (precision 1, recall 1). `callers duration` recall is 1 and precision is 0.125, because the query records the name `duration` wherever it is called.

**`find` LIKE metacharacters.** `find _isoDuration` returns one symbol, precision 1, recall 1. On 2026-11-02 the underscore matched the `d` in `$ZodISODuration` and precision was 0.09.

**No project at the repo root.** With the clone unshallowed and `src/Humanizer.slnx` restored, a command with no `--solution` prints `symdex: using 'src/Humanizer.slnx' (no solution at the repo root; pass --solution to override).` and indexes with Roslyn. `callers ToFullWords` then returns test call sites (50, truncated, precision 1). The 2026-11-02 message was `symdex: no .sln/.csproj found at repo root.`

### Still present

**Shallow clone plus Nerdbank.GitVersioning.** A depth-1 clone of Humanizer `v3.0.10`, `dotnet restore src/Humanizer/Humanizer.csproj`, then `symdex --repo <clone> --solution src/Humanizer/Humanizer.csproj --json --limit 3 find ByteSize --kind struct` still fails the workspace load. Stderr contains `symdex: workspace load reported 1 failure(s)` and `Shallow clone lacks the objects required to calculate version height`, then `reindexed 534 files`. The hit's `fqName` is `ByteSize`. The scored run avoids this by unshallowing before the first query. This probe is not a row in `results/2026-11-03.json`.

**Library `--solution` omits tests.** The comparable Humanizer command still points at `Humanizer.csproj` only, so `callers ToFullWords` is empty. That is the same command as 2026-11-02, kept so the two runs compare. The supplementary solution run is the one that includes the tests, and it is limited by `--limit 50` before the expected set ends.

**Roslyn reindex of one file is still seconds.** Correctness of the kept refs is fixed. The 3.7 s and 6.4 s reloads are the remaining cost.

**Name-only and substring matches.** `find ByteRa`, `find requote`, `callers duration`, `callers send`, and import-as-caller are the losses in the tables above. Tree-sitter refs are by identifier text. `find` still matches fqname substrings for queries that are not `LIKE` metacharacters.

## Limitations

This is a rule-based stand-in for an agent, not a model run. The read rule (first 5 distinct grep files, whole file; first 20 symdex spans, 12 lines each) is fixed in `tasks.json` and will not match every agent's judgment. A person who stopped at the right file sooner would shrink the baseline. A person who opened every symdex hit past the cap of 20 would grow the symdex side. `callers-session-send` and `outline-sessions` are the questions where that cap binds on the 22-question set. The supplementary `ToFullWords` page binds on `--limit 50` before the span cap does.

Tokens are bytes/4, labelled as an approximation. One machine, one OS, Release CLI process per query. MCP would avoid process startup; that latency was not measured. Warm times of ~155–200 ms are mostly startup plus hashing.

Ground truth was written by hand against the pinned commits. The one correction this round is `iso.ts:56`, described above. Baseline recall of 1.00 on outlines means the file contains the expected lines.

Not measured: plain-text CLI output (JSON only), other candidate repos, in-process MCP, and any run where the model chooses the tool arguments itself. Symdex was given `--kind` on the locate-a-type questions; grep was given the bare name.
