# `src/RhinoDB.Lib/Execution/DbExecutionLoop.cs` — dev notes

## Rewritten 2026-09-14 — pooled `ValueTask`/`IValueTaskSource` execution engine

Previously this file held four near-identical ~34-line `Enqueue` overloads,
each building its own `TaskCompletionSource<Result>`/`Result<T>` and writing
a hand-rolled closure `Action` to `Channel<Action>` (the `txCreated`-flag/
`Discard()`-on-error exception-safety logic, and the `Complete`/`Finalize`
durability-sync-result-folding logic, were duplicated four times over). All
of that moved into the single generic `PooledOperation<TTx,TValue,TArgs>`
(`Docs/Dev/RhinoDB.Lib/Execution/PooledOperation.md`) - see that file for the
`txCreated`/`Discard()` and `Complete`/`Finalize` rationale, both reproduced
there verbatim, not simplified away during the collapse from four copies to
one.

What's left here is now genuinely small: the channel changed from
`Channel<Action>` to `Channel<IExecutionWorkItem<TTx>>` (same
`UnboundedChannelOptions { SingleReader = true, SingleWriter = false }`,
unchanged - this is a property of `Channel<T>` itself, not of what `T` is),
and each `Enqueue` overload is now a one-line forward to
`PooledOperation<TTx,TValue,TArgs>.Enqueue(...)`. The two no-`TArgs`
overloads (`Enqueue`/`Enqueue<T>`) forward through the *with-args* pooled
shape using the caller's own delegate type as `TArgs`, via a cached `static`
trampoline (`static (ctx, tx, op) => op(ctx, tx)`) rather than a second
pooled type or a `Unit` sentinel - non-capturing, so per
`Docs/01-performance-principles.md` §3 it's allocated once per closed
`(TTx, TValue)` shape and reused forever, not once per call.

Full measured result, design rationale (why the channel element is a pooled
*reference*, not a literal value-type struct - `IValueTaskSource` completion
must mutate the same instance the caller is awaiting, which a struct-by-value
element can't support), and the two real `ValueTask`-vs-`Task` contract
gotchas hit while wiring this up (single-consumption; no blocking synchronous
wait) are in `Docs/01-performance-principles.md` § "No closures on the hot
path" and `Docs/03-roadmap.md`'s 2026-09-14 entry - this file's own notes stay
narrowly about what physically changed in `DbExecutionLoop.cs` itself.
