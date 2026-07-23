---
name: symdex
description: >
  Before grepping, reading files to explore structure, or searching for symbols in this
  codebase, use `symdex`. It returns symbol locations, signatures, and reference sites far
  cheaper than reading files. Read only the line spans it reports.
---

# symdex

A local codebase index. Query it instead of grepping or opening whole files to find out where
something is defined or used - it returns symbol locations, signatures, and reference sites, not
code bodies. Read only the line spans it reports.

## `symdex outline <file>`

All symbols declared in a file, in order, with their signature and line span - no bodies.

```
$ symdex outline src/payments/PaymentProcessor.cs
class PaymentProcessor public sealed class PaymentProcessor : IPaymentProcessor :12-58
method Charge public Task<Receipt> Charge(Money amount, Card card) :24-31
method Refund public Task<Receipt> Refund(string receiptId) :33-40
... +2 more (narrow the query)
```

## `symdex find <name> [--kind <kind>]`

Substring + full-text search over symbol names, fully-qualified names, and doc comments.

```
$ symdex find PaymentProcessor
class Payments.PaymentProcessor public sealed class PaymentProcessor : IPaymentProcessor src/payments/PaymentProcessor.cs:12-58
interface Payments.IPaymentProcessor public interface IPaymentProcessor src/payments/IPaymentProcessor.cs:5-9
```

## `symdex refs <fqname-or-name>`

Every reference site for a symbol (declarations, usages, implementations), grouped under the
resolved symbol.

```
$ symdex refs IPaymentProcessor
Payments.IPaymentProcessor
implementation src/payments/PaymentProcessor.cs:12
reference src/payments/CheckoutService.cs:18
... +6 more (narrow the query)
```

## `symdex callers <fqname-or-name>`

Call/reference sites for a method or function - "who calls this."

```
$ symdex callers Charge
call src/payments/CheckoutService.cs:22
call tests/PaymentProcessorTests.cs:41
```

## The index self-manages

Every command above re-hashes changed files and refreshes the database inline before answering -
there is no separate build or refresh step. Never run a manual "index" or "build" command; just
call the verb you need.
