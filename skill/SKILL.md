---
name: symdex
description: >
  Before grepping, reading files to explore structure, or searching for symbols in this
  codebase, use `symdex`. It returns symbol locations, signatures, and reference sites far
  cheaper than reading files. Read only the line spans it reports.
---

# symdex

A local codebase index for C#, TypeScript/JavaScript, and Python. Query it instead of grepping or
opening whole files to find out where something is defined or used - it returns symbol locations,
signatures, and reference sites, not code bodies. Read only the line spans it reports.

Paths are relative to the repo root. Add `--json` for one JSON object per line, and `--limit <n>`
(max 200) to widen a truncated result.

## `symdex outline <file>`

All symbols declared in a file, in order, with their signature and line span - no bodies.

```
$ symdex outline csharp/SampleLib/PaymentProcessor.cs
class PaymentProcessor class PaymentProcessor :4-11
method Charge public decimal Charge(decimal amount) :7-10
```

## `symdex find <name> [--kind <kind>]`

Substring + full-text search over symbol names, fully-qualified names, and doc comments. Each line
is `kind fqname signature path:start-end`.

```
$ symdex find Charge --kind method
method System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal) decimal Charge(decimal amount) csharp/SampleLib/IPaymentProcessor.cs:7-7
method System.Decimal SampleLib.PaymentProcessor.Charge(System.Decimal) public decimal Charge(decimal amount) csharp/SampleLib/PaymentProcessor.cs:7-10
```

## `symdex refs <fqname-or-name>`

Every reference site for a symbol (declarations, usages, implementations), grouped under the
resolved symbol.

```
$ symdex refs IPaymentProcessor
SampleLib.IPaymentProcessor
reference csharp/SampleLib/CheckoutService.cs:6
reference csharp/SampleLib/CheckoutService.cs:9
reference csharp/SampleLib/PaymentProcessor.cs:4
implementation csharp/SampleLib/PaymentProcessor.cs:4
```

## `symdex callers <fqname-or-name>`

Call sites for a method or function - "who calls this."

```
$ symdex callers 'System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)'
System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)
call csharp/SampleLib/CheckoutService.cs:17
```

## Ambiguous names

If a plain name matches more than one symbol, `refs`/`callers` print the candidates and exit with
code 2. Re-run with one candidate copied exactly - for C# methods the fully-qualified name
includes the return type and parameter types, and a shortened form such as
`SampleLib.IPaymentProcessor.Charge` matches nothing.

```
$ symdex callers Charge
ambiguous symbol 'Charge', candidates:
  System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)
  System.Decimal SampleLib.PaymentProcessor.Charge(System.Decimal)
```

## Precision differs by language

C# in a restored solution is indexed semantically with Roslyn: `refs`/`callers` are bound to the
exact symbol. TypeScript, JavaScript, and Python (and C# when the solution can't be loaded) are
indexed syntactically: their symbols have no fully-qualified name, and `refs`/`callers` match by
name, so every identifier with that spelling - in any of those languages - is reported. Check the
reported lines before relying on them.

```
$ symdex callers helper
helper
reference py/app.py:1
call py/app.py:5
call py/calc.py:16
reference ts/math.ts:1
```

## The index self-manages

Every command above re-hashes changed files and refreshes the database inline before answering -
there is no separate build or refresh step. Never run a manual "index" or "build" command; just
call the verb you need.
