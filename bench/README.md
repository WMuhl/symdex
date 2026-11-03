# Symdex benchmark

On 22 questions across three pinned repos, symdex's JSON plus the source spans it pointed at was **49,769 bytes**. The grep-or-read baseline was **2,357,421 bytes**. Ratio **0.0211** (about 12,400 vs 589,000 tokens at bytes/4). Mean precision where symdex returned locations was **0.827** (18 questions); mean recall across all 22 was **0.786**. The baseline's mean precision on grep questions was **0.354**, and its recall was **1.00**.

That byte ratio is the headline. It is not uniform. Two small-file outlines cost more than reading the file. Tool calls went the other way (126 vs 89). Four questions returned no locations.

Raw rows are in [`results/2026-11-02.json`](results/2026-11-02.json). Questions and expected `file:line` sets are in [`tasks.json`](tasks.json).

## How to run

From the repo root, after `dotnet build symdex.sln -c Release`:

```powershell
pwsh -NoProfile -File .\bench\run-bench.ps1 -Date 2026-11-02
```

The script is PowerShell 7 (`#requires -Version 7`). It is not part of `symdex.sln`. Clones go to `$env:TEMP\symdex-bench\work` (override with `-WorkRoot`). That directory is deleted and recreated at the start of every run. `-Only <id>` runs one target. The symdex binary defaults to `src/Symdex.Cli/bin/Release/net10.0/symdex.exe`.

## Methodology

Each target is a shallow `git clone --depth 1 --branch <tag>`. Humanizer is then `git fetch --unshallow` (see below). The script checks `HEAD` against the commit in `tasks.json`. Prep commands run next (Humanizer: `dotnet restore`). `.index/` is deleted so the first query is cold.

Context bytes are UTF-8 of stdout. Stderr (`reindexed N files`, warnings) is not context. Approximate tokens are bytes/4.

### Symdex path

The measured output is `--json --limit 50` (the CLI default limit; the hard cap is 200). Predictions are the unique `file:startLine` values for `find` and `outline`, and `file:line` for `refs` and `callers`.

An agent then reads those spans, with three caps:

- identical spans are read once
- the first 20 distinct spans (`maxSpans`)
- each span is at most 12 lines from its start line (`spanCapLines`); the raw file bytes of those lines are counted, including newlines
- overlapping or adjacent spans in one file are merged before the byte count

Tool calls = 1 (the symdex invocation) + the number of merged spans. Questions with more than 20 distinct locations are scored on every location, but bytes and tool calls stop at the cap. Two questions hit that cap: `outline-sessions` (31 locations) and `callers-session-send` (43).

### Grep path

`git grep -n -I -F -- <term>` over the whole clone, tracked files, including docs and tests. Predictions are the unique `file:line` hits.

The read rule: open, in full, every distinct file among the first 5 hits, in first-appearance order (`grepK` = 5). Tool calls = 1 + files opened. Bytes = grep stdout + those file sizes.

### Outline path

The baseline is one full-file read, not grep. Recall is the fraction of expected lines that sit in that file (1.00 here, because every expected line was chosen inside the file). Precision is null, because a read does not extract locations. Those nulls are omitted from the baseline precision mean. They are included in baseline recall, which is why that mean is 1.00 even before counting grep. Every grep question in this set also had baseline recall 1.00: each expected line contains the fixed string that was grepped.

### Scoring

Expected locations answer the natural-language question. They are not "every row the tool's matcher is specified to emit." A partial-name search that returns members is a false positive if the question asked for the type. An import is a false positive if the question asked for callers. The declaration line is not a call.

Precision and recall are on the unique `file:line` sets. Precision is null when a path returns no locations and the expected set is non-empty. The published mean skips those nulls. Treating them as precision 0 gives a strict mean of **0.677** over all 22 questions (the defined-only mean is 0.827 over 18). Both-empty would be precision 1 and recall 1; that did not occur. False positives in the JSON are capped at 25 per question; the counts `fp` / `fn` are complete.

### Latency

- **Cold** is the first question's first invocation on a fresh clone after restore and unshallow, with `.index/` absent. It includes process startup, indexing, and that query. Clone, unshallow, and `dotnet restore` are timed separately and are not part of cold time.
- **Warm** is the median of 5 further invocations of the same question, no file changes. Median is the lower middle of the sorted sample, index `(n-1)/2`. Warm stderr did not contain `reindexed`. The time includes CLI startup and hashing every scanned file. An in-process MCP call was not measured.
- **Incremental** runs after the scored questions. The watch query is snapshotted, one newline is appended to one file (existing line numbers stay put), and the same query is timed. `watchedLinesDropped` is true when that file had locations before the touch and none after.

A `symdex:` line on the C# cold stderr means Roslyn declined and tree-sitter indexed those files. This run had none (`roslynFallback: false`).

## Machine and commits

| | |
|---|---|
| OS | Windows NT 10.0.26200.0 |
| PowerShell | 7.6.9 |
| CPU | AMD Ryzen 9 9950X3D 16-Core |
| RAM | 61.58 GB |
| `dotnet --version` | 10.0.204 (SDKs 8.0.427 and 10.0.204 also installed) |
| Worktree HEAD at measurement | `c905dd091a0108ffc1873a5f7d248550ab2bd2d3` (`bench/harness`) |
| Product source under that commit | `0bcf298` (`src/` unchanged by the bench commits) |
| Binary | `src/Symdex.Cli/bin/Release/net10.0/symdex.exe` |

`results/2026-11-02.json` records `symdexCommit` as the HEAD above. The results commit is the one that added this file; it does not change `src/`.

## Targets

| id | repo | tag | commit | clone |
|---|---|---|---|---|
| humanizer | Humanizr/Humanizer | v3.0.10 | `90f0cb1d7427635f38b46350bb1dc5fca2164f2d` | shallow, then unshallow |
| zod | colinhacks/zod | v4.6.7 | `b500a2ee95134f81d74da7911d7378dd2853d042` | shallow |
| requests | psf/requests | v2.32.5 | `b25c87d7cb8d6a18a37fa12442b5f883f9e41741` | shallow |

Humanizer has no `.sln` or `.csproj` at the repo root. The harness passes `--solution src/Humanizer/Humanizer.csproj`. A depth-1 clone makes MSBuildWorkspace fail: Nerdbank.GitVersioning cannot compute version height, Roslyn aborts, and symdex falls back to tree-sitter. The harness unshallows before restore. `dotnet restore src/Humanizer/Humanizer.csproj` exited 0. The project multi-targets `net10.0;net8.0;net48;netstandard2.0`. Test projects are not in that compilation; the scanner still records the test `.cs` files with no symbols.

Zod and requests need no install. Tree-sitter does not execute TypeScript or Python. Newtonsoft.Json, command-line-api, ky, and Flask were not needed.

Prep, not included in cold time: Humanizer clone 987 ms, unshallow 2057 ms, restore 823 ms. Zod clone 1155 ms. Requests clone 970 ms.

## Results

What `run-bench.ps1` printed. Ratios and precision in this table are formatted to two decimal places, so 0.0127 prints as 0.01. The next table uses the JSON values.

```
target          cold_ms   index_kb  sym_bytes  base_bytes  ratio  sym_P  sym_R  base_P  base_R
humanizer         28338     33056      11652      915539   0.01  0.86  0.76  0.24  1.00
zod                6116      1132      12980      780161   0.02  0.77  0.57  0.34  1.00
requests           1444       924      25137      661721   0.04  0.83  1.00  0.45  1.00
OVERALL sym=49769 base=2357421 ratio=0.0211 P=0.8271865946284551 R=0.7857142857142857 baseP=0.3537671798181878 baseR=1 n=22
```

| target | cold | files | index | sym bytes | base bytes | ratio | sym P | sym R | base P | base R | warm median |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| humanizer | 28.3 s | 534 | 33,849,344 | 11,652 | 915,539 | 0.0127 | 0.861 | 0.755 | 0.245 | 1.00 | 197 ms |
| zod | 6.1 s | 513 | 1,159,168 | 12,980 | 780,161 | 0.0166 | 0.773 | 0.571 | 0.345 | 1.00 | 193 ms |
| requests | 1.4 s | 36 | 946,176 | 25,137 | 661,721 | 0.0380 | 0.829 | 1.000 | 0.453 | 1.00 | 164 ms |
| overall | | | | 49,769 | 2,357,421 | 0.0211 | 0.827 | 0.786 | 0.354 | 1.00 | |

Precision means skip nulls. Symdex's nulls are the four empty answers (strict mean 0.677 if those count as 0). Baseline precision is grep questions only. Approximate tokens (bytes/4): symdex 12,442, baseline 589,355. Tool calls: symdex 126, baseline 89.

Cold stderr was `reindexed N files` only. No `symdex:` warning, so the C# rows are Roslyn.

### Per question

`warm` is the median of 5. `dup` is raw JSON rows / unique lines. `calls` is symdex/baseline. A blank baseline precision is an outline (full-file read).

#### Humanizer (C#, Roslyn)

| question | verb | warm | raw/uniq | dup | sym P | sym R | base P | ratio | sym B | base B | calls |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| locate-bytesize | find | 200 | 1/1 | 1 | 1.00 | 1.00 | 0.001 | 0.0031 | 593 | 191,361 | 2/6 |
| locate-default-formatter | find | 198 | 1/1 | 1 | 1.00 | 1.00 | 0.013 | 0.0082 | 543 | 66,298 | 2/6 |
| partial-byterate | find | 200 | 6/6 | 1 | 0.17 | 1.00 | 0.053 | 0.0123 | 2,498 | 203,463 | 5/6 |
| outline-truncator | outline | 195 | 6/6 | 1 | 1.00 | 1.00 | | 1.4314 | 1,566 | 1,094 | 5/1 |
| outline-iformatter | outline | 197 | 9/9 | 1 | 1.00 | 1.00 | | 0.8466 | 1,783 | 2,106 | 7/1 |
| refs-itruncator | refs | 195 | 50/4 | 12.5 | 1.00 | 0.29 | 0.280 | 0.0634 | 4,571 | 72,058 | 5/6 |
| callers-tofullwords | callers | 195 | 0/0 | | | 0.00 | 0.876 | 0.0003 | 98 | 379,159 | 1/6 |

`partial-byterate` false positives are members of `ByteRate` and `ByteSizeExtensions.Per` (fqname contains `ByteRa`). `refs-itruncator` is truncated: the 4 lines that fit are all in `TruncateExtensions.cs`; the five truncator implementations and the `Truncator` property lines are missing (recall 4/14). `callers-tofullwords` expected 78 call sites, all under `tests/`. The loaded project is the library only, so those files have no symbols. Grep recall on that question is 1.00 at precision 0.88.

#### Zod (TypeScript, tree-sitter)

| question | verb | warm | raw/uniq | sym P | sym R | base P | ratio | sym B | base B | calls |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| locate-iso-duration-fn | find | 197 | 11/11 | 0.09 | 1.00 | 0.250 | 0.0268 | 4,541 | 169,262 | 8/5 |
| locate-mini-iso-duration-type | find | 195 | 1/1 | 1.00 | 1.00 | 0.167 | 0.0893 | 366 | 4,098 | 2/3 |
| partial-mini-iso | find | 193 | 8/8 | 1.00 | 1.00 | 0.320 | 0.1347 | 3,581 | 26,578 | 5/4 |
| outline-mini-iso | outline | 189 | 12/12 | 1.00 | 1.00 | | 1.9128 | 4,409 | 2,305 | 9/1 |
| refs-mini-iso-duration | refs | 196 | 0/0 | | 0.00 | 0.500 | 0.0081 | 33 | 4,098 | 1/3 |
| callers-iso-duration-helper | callers | 191 | 0/0 | | 0.00 | 0.750 | 0.0002 | 27 | 169,262 | 1/5 |
| callers-mini-duration | callers | 193 | 0/0 | | 0.00 | 0.083 | 0.0001 | 23 | 404,558 | 1/6 |

`find _isoDuration` returns the real function (`packages/zod/src/v4/core/api.ts:598`) and ten other symbols. `_` is an unescaped SQL `LIKE` wildcard, so the query also matches `$ZodISODuration`. Grep precision on that question is 0.25, higher than symdex. The three `refs`/`callers` questions print only the symbol header. Expected sites exist (`iso.ts` uses, `external.ts` re-export, classic call sites, two `z.iso.duration()` tests).

#### Requests (Python, tree-sitter)

| question | verb | warm | raw/uniq | sym P | sym R | base P | ratio | sym B | base B | calls |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| locate-prepared-request | find | 157 | 1/1 | 1.00 | 1.00 | 0.017 | 0.0045 | 674 | 151,066 | 2/6 |
| outline-sessions | outline | 164 | 31/31 | 1.00 | 1.00 | | 0.3954 | 12,391 | 31,334 | 20/1 |
| outline-api | outline | 165 | 8/8 | 1.00 | 1.00 | | 0.7511 | 4,962 | 6,606 | 9/1 |
| callers-merge-setting | callers | 164 | 8/8 | 1.00 | 1.00 | 0.889 | 0.0306 | 983 | 32,099 | 5/2 |
| refs-requote-uri | refs | 169 | 7/7 | 1.00 | 1.00 | 0.778 | 0.0057 | 766 | 133,894 | 8/5 |
| partial-requote | find | 164 | 2/2 | 0.50 | 1.00 | 0.111 | 0.0115 | 1,542 | 133,894 | 3/5 |
| callers-dispatch-hook | callers | 165 | 3/3 | 0.67 | 1.00 | 0.500 | 0.0128 | 420 | 32,900 | 4/4 |
| callers-session-send | callers | 159 | 43/43 | 0.47 | 1.00 | 0.426 | 0.0243 | 3,399 | 139,928 | 21/6 |

Python `refs` and `callers` returned the expected lines, including test files. `partial-requote` also returns `test_requote_uri_with_unquoted_percents` (the name contains `requote`). `callers-dispatch-hook` counts the import at `sessions.py:30`, because callers include reference rows. `callers-session-send` returns every `.send` call the tree-sitter query sees (`adapter.send`, `connection.send`, `sock.send`), 23 false positives, recall 1.00 of the 20 `Session.send` sites. Grep of `.send(` is similarly imprecise (0.43) and much larger. Span reads for this question stopped at 20 of 43 locations.

### Where symdex lost

Shown above, collected so a mean cannot hide them:

- **Bytes.** `outline-truncator` 1,566 vs 1,094 (ratio 1.43, 5 calls vs 1). `outline-mini-iso` 4,409 vs 2,305 (ratio 1.91, 9 vs 1). `Truncator.cs` is 32 lines; `mini/iso.ts` is 66. Reading each reported span, with a JSON header per symbol, costs more than reading the file once.
- **Tool calls.** 126 vs 89 overall. Outlines dominate: `outline-sessions` is cheaper in bytes (ratio 0.40) and costs 20 calls against one read. `outline-iformatter` and `outline-api` are also cheaper in bytes and more expensive in calls.
- **Empty answers that look cheap.** Zod `refs`/`callers` are 23–33 bytes and recall 0. `callers-tofullwords` is 98 bytes and recall 0, against a grep baseline that finds all 78 test call sites.
- **Truncated duplicate refs.** `refs ITruncator`: 50 rows, 4 unique lines, duplicate factor 12.5, `truncated: true`, precision 1.00 on those 4, recall 0.29 of 14.
- **Precision below grep.** `find _isoDuration` precision 0.09 vs grep 0.25. The function is in the result (recall 1), buried under LIKE wildcard hits.
- **Partial names and common names.** `find ByteRa` precision 0.17 (grep 0.05). `find requote` precision 0.50 (grep 0.11). `callers send` precision 0.47 (grep 0.43). Symdex is smaller in every one of these, and still returns the wrong lines along with the right ones. `callers dispatch_hook` precision 0.67 because the import is a "caller."

Locate-a-type with `--kind` is the clean win: `ByteSize`, `DefaultFormatter`, `ZodMiniISODuration`, and `PreparedRequest` are precision 1 and recall 1, at 0.3% to 9% of the grep-and-read byte cost. Grep precision on `ByteSize` is 0.001 because the name is everywhere and the baseline then reads five whole files.

### Latency

Warm medians sit between 157 ms and 200 ms. By verb, the lower-middle of those medians: find 195 ms (8 questions, 157–200), outline 189 ms (5, 164–197), refs 195 ms (3, 169–196), callers 165 ms (6, 159–195). Process startup is inside every sample.

| | cold | index | incremental |
|---|---:|---:|---|
| humanizer | 28,338 ms | 33.8 MB, 534 files | 6,373 ms, `reindexed 1 files`, 4 watched lines dropped |
| zod | 6,116 ms | 1.16 MB, 513 files | 227 ms, `reindexed 1 files`, no refs to drop |
| requests | 1,444 ms | 0.95 MB, 36 files | 211 ms, `reindexed 1 files`, all 8 call lines kept |

The Humanizer incremental touch was a trailing newline on `src/Humanizer/TruncateExtensions.cs`. Before: lines 38, 56, 96, 117. After: none. The requests touch was the same kind of edit on `src/requests/sessions.py`; `callers merge_setting` still returned 103, 490, 493, 494, 774, 775, 776, 777.

## Bugs and slowness

Nothing here was patched in `src/` for this run. Numbers are from this run unless a line says it was a setup probe. TypeScript call and reference indexing landed afterward; see bug 3.

### 1. Multi-target projects store the same reference many times

Humanizer's library project targets four frameworks. `refs ITruncator` at `--limit 50` printed 50 rows and 4 unique lines (`TruncateExtensions.cs` 38, 56, 96, 117), set `truncated`, and never reached the implementation files. Duplicate factor inside that window is 12.5. Cold index of the same project was 28.3 s and 33.8 MB.

`RoslynWorkspaceIndexer` calls `SymbolFinder.FindReferencesAsync` once per symbol declared in a changed file and keeps every hit whose file is in the change set (`AddRefIfChanged`). A multi-targeted project compiles that file once per TFM, and `IndexDatabase` does not dedupe ref rows. The same path stores method references as kind `call`, so `callers` and `refs` overlap for methods.

Repro: full clone of Humanizer `v3.0.10` (a shallow clone takes bug 5's fallback instead), `dotnet restore src/Humanizer/Humanizer.csproj`, then `symdex --repo . --solution src/Humanizer/Humanizer.csproj --json --limit 50 refs ITruncator`.

The database row count behind the limit window was not queried.

### 2. Reindexing one file drops its outgoing cross-file refs

After the index exists, appending a newline to `src/Humanizer/TruncateExtensions.cs` and running `refs ITruncator` prints `reindexed 1 files` and takes 6,373 ms. The four `ITruncator` lines in that file disappear.

`referenceableSymbols` only contains symbols declared in the changed file. `ITruncator` is declared elsewhere, so the re-feed of `TruncateExtensions.cs` does not search for references to it. `ReplaceSymbolsAndRefs` deletes every ref row for that `file_id` first (`IndexDatabase.cs`). Tree-sitter re-parses the touched file and re-emits name-based refs; the requests incremental case kept its eight lines in 211 ms. The 6.4 s is a Roslyn workspace reload for a one-line edit.

### 3. TypeScript queries did not record calls or identifier references

Fixed after this run. `queries/typescript.scm` now records calls, `new`, identifier and type references, and `extends` / `implements` as `implementation`. The zod `refs` / `callers` rows in the table above are from before that change: stdout was the symbol header only (33, 27, and 23 bytes). Recall 0. `find` and `outline` on the same repo matched the expected definitions.

At the time of the run, `queries/typescript.scm` ended at import patterns. `queries/python.scm` already had call and identifier patterns, which is why the requests `refs` and `callers` questions returned rows and the zod ones did not.

### 4. `find` does not escape `LIKE` metacharacters

`IndexDatabase.Find` binds `$"%{query}%"`. `_` is one character and `%` is any sequence. `find _isoDuration` on zod returned 11 symbols, precision 0.09 against the one function at `packages/zod/src/v4/core/api.ts:598`. The extra hits are `$ZodISODuration` and similar names, because `_` matches the `d` in `Zod`.

### 5. Roslyn is skipped when the repo root has no project, or the clone is shallow

`SolutionResolver` only looks at the repo root for a single `.sln` or a single `.csproj`. Humanizer has neither. Without `--solution`, stderr contains `symdex: no .sln/.csproj found at repo root.` and the C# files go through tree-sitter.

A depth-1 clone plus `--solution` still falls back. Nerdbank.GitVersioning throws because the shallow clone cannot compute version height; any workspace load failure aborts the Roslyn feed. Observed while choosing the target setup (not repeated in the scored run): shallow Humanizer reindexed 534 files into a ~2.3 MB database in about 11 s, and fully-qualified names were short names. The scored run unshallows first. Cold stderr was `reindexed 534 files` and `roslynFallback` is false.

Passing `--solution src/Humanizer/Humanizer.csproj` also omits `Humanizer.Tests`. `callers ToFullWords` therefore returns nothing (78 expected call sites, all in tests, recall 0). A solution that includes the test projects was not tried. A compile error anywhere aborts the whole Roslyn feed, so a tests-inclusive solution that does not build cleanly would fall back entirely.

## Limitations

This is a rule-based stand-in for an agent, not a model run. The read rule (first 5 distinct grep files, whole file; first 20 symdex spans, 12 lines each) is fixed in `tasks.json` and will not match every agent's judgment. A person who stopped at the right file sooner would shrink the baseline. A person who opened every symdex hit past the cap of 20 would grow the symdex side. `callers-session-send` and `outline-sessions` are the questions where that cap binds.

Tokens are bytes/4, labelled as an approximation. One machine, one OS, Release CLI process per query. MCP would avoid process startup; that latency was not measured. Warm times of ~160–200 ms are mostly startup plus hashing.

Ground truth was written by hand against the pinned commits and stored before scoring. `outline-iformatter` expected lines 28 and 44 are the method declarations (`TimeSpanHumanize`, `DataUnitHumanize`), which is what the outline returns. Baseline recall of 1.00 on outlines means the file contains the expected lines.

Not measured: plain-text CLI output (JSON only), other candidate repos, a Humanizer solution that includes tests, in-process MCP, and any run where the model chooses the tool arguments itself. Symdex was given `--kind` on the locate-a-type questions; grep was given the bare name. That is the realistic pair (the agent can pass a kind; grep has no kind), and it favors symdex on those four questions.
