# `src/RhinoDB.Lib/Execution/DbContext.cs` — dev notes

## `Run`/`RunConfirmed` return `ValueTask<Result>`/`ValueTask<Result<T>>`, not `Task` (2026-09-14)

Part of the pooled-execution-engine redesign
(`Docs/01-performance-principles.md` § "No closures on the hot path",
`Docs/Dev/RhinoDB.Lib/Execution/PooledOperation.md`). A deliberate, real
breaking API change, not an implementation detail: keeping `Task<Result>` as
the public return type while pooling the completion mechanism internally
would force a real `Task` allocation via `.AsTask()` on *every* call,
defeating the entire point of pooling. `ValueTask` gets the full allocation
win for the dominant call shape (fire, then immediately `await` - the common
case everywhere in this codebase's own tests/benchmarks); a caller doing
genuine `Task.WhenAll`-style concurrent fan-out adds `.AsTask()` explicitly,
a one-line cost paid only where it's actually needed.

**Real behavioral differences from `Task` any consumer needs to know** (both
hit and fixed during this same redesign, both detailed in
`Docs/Dev/RhinoDB.Lib/Execution/PooledOperation.md`): `ValueTask<T>` is
single-consumption (await/`.AsTask()` exactly once - awaiting twice throws);
and it does not support a blocking synchronous wait the way
`Task<T>.GetAwaiter().GetResult()` does (call `.AsTask()` first if a genuine
synchronous block is needed).

## Non-generic `DbContext`'s `Run` overloads - the double-closure fix

Before this change, every non-generic `DbContext.Run(...)` call wrapped the
caller's delegate in a *second* adapter closure (`(ctx, _) => func((DbContext)ctx)`)
before forwarding to the generic `DbContext<TTx>.Run` base - meaning every
call through the common, non-generic `DbContext` (used throughout
`DbContextTests.cs` and any hand-written game code not needing a custom
`TTx`) paid for two allocated closures per call, not one, even before
`DbExecutionLoop`'s own per-call closure was in the picture. Fixed the same
way the rest of this redesign avoids closures generally: a cached `static`
trampoline plus the caller's own delegate (or, for the `TArgs` overloads, a
value-tuple combining the delegate and the original `args` into one
`TArgs`) threaded through explicitly, so nothing is captured at all.

```csharp
public ValueTask<Result> Run(Func<DbContext, Result> func, PropagationMode mode = PropagationMode.Optimistic)
    => base.Run(static (ctx, _, f) => f((DbContext)ctx), func, mode);
```

The `TArgs`-taking overloads (`Run<TArgs>`/`Run<T,TArgs>`) need to thread
*two* values (the caller's own delegate and its `args`) through a base method
that only accepts one `TArgs` - solved with a plain value tuple
(`(Func<DbContext,TArgs,Result> Func, TArgs Args)`) as the new, combined
`TArgs` for the base call. `ValueTuple` is a struct - no boxing, no new
allocation beyond what threading two values together always costs.
