# `src/RhinoDB.Sandbox.Benchmark/Benchmarks/InstantTableBenchmarks.cs` — dev notes

## `GetArgs`/`InsertArgs`/`UpdateArgs`

Added 2026-09-14 alongside `Docs/01-performance-principles.md`'s "No closures
on the hot path" resolution. Same operations as `Get`/`Insert`/`Update`, but
using the `Run<TArgs>`/`Run<T,TArgs>` overloads with a `static` lambda and
explicit `args` instead of an implicitly-captured local (`id`/`lookupKey`) -
isolates how much of `Get`/`Insert`/`Update`'s measured allocation is the
*caller's own* avoidable closure versus the floor `DbExecutionLoop.Enqueue`
itself always pays. Measured: `Get` 353 B → `GetArgs` 297 B, `Insert` 361 B →
`InsertArgs` 281 B, `Update` 321 B → `UpdateArgs` 273 B - a real, ~15-22%
reduction with zero engine changes, purely from the calling convention. Kept
side by side permanently (not deleted once the question was answered) as a
living measurement anyone can re-run after a future engine change to this
path, and as a concrete example of the `Run<TArgs>` pattern this project
recommends for any call site with per-call captured state.

**Superseded (numbers, not the recommendation) 2026-09-14** by the pooled
`ValueTask`/`IValueTaskSource` execution engine
(`Docs/Dev/RhinoDB.Lib/Execution/PooledOperation.md`): re-measured after that
redesign landed, `Get` 353→**152 B**, `Insert` 361→**176 B**, `Update`
321→**136 B**, `GetArgs` 297→**96 B**, `InsertArgs` 281→**96 B**, `UpdateArgs`
273→**80 B** - `Run<TArgs>` + `static` lambdas is still the right calling
convention (it's still measurably cheaper than the plain overload on the new
mechanism too), just against a much lower floor now.

## `Setup()` — `.AsTask()` before `.GetAwaiter().GetResult()`

Added 2026-09-14, required, not stylistic: `ValueTask<T>` (the type `Run` now
returns) does not support a blocking synchronous wait the way
`Task<T>.GetAwaiter().GetResult()` does - calling it directly, synchronously,
right after enqueueing (before the operation has actually run on the
background loop) throws `InvalidOperationException`. `.AsTask()` produces a
real `Task<T>`, which does support blocking wait. See
`Docs/Dev/RhinoDB.Lib/Execution/PooledOperation.md` for the full mechanism
and why this is a genuine `ValueTask`-vs-`Task` contract difference, not a
bug in this file - the same fix applies to any RhinoDB-consuming code doing
a genuine synchronous block on a `Run` result.
