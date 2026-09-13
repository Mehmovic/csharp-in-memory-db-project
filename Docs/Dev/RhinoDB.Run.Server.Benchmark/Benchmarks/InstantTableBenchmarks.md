# `src/RhinoDB.Run.Server.Benchmark/Benchmarks/InstantTableBenchmarks.cs` — dev notes

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
path, and as a concrete example of the `Run<TArgs>` pattern this project now
recommends for any call site with per-call captured state.
