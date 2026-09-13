# `src/RhinoDB.Run.Server.Benchmark/Benchmarks/SubmissionOverheadBenchmarks.cs` — dev notes

Added 2026-09-14 to decompose `DbContext.Run`'s per-call allocation floor by
measurement rather than by estimating object layouts by hand
(`Docs/01-performance-principles.md` § "No closures on the hot path").

## `BareTaskCompletionSource`

Isolates the cost of `new TaskCompletionSource<Result>(TaskCreationOptions
.RunContinuationsAsynchronously)` plus the `Task<Result>` it owns internally,
with nothing else around it (no channel, no `DbExecutionLoop` involvement at
all) - this is the unavoidable cost of `Run`'s own `Task<Result>`-returning
public contract. Measured: 88 B. Anything above this in a real `Run` call is
attributable to the execution-loop's own submission machinery, not to the
`Task` itself.

## `BareClosureOverEnumAndInt`

Isolates the cost of a minimal capturing closure (a `PropagationMode` and an
`int`, returned as an `Action` so the compiler can't optimize the allocation
away) - a rough proxy for what `DbExecutionLoop.Enqueue`'s own internal
closure (which captures `context`/`operation`/`args`/`mode`/`tcs` - more
fields than this proxy, so its real cost is somewhat higher) costs regardless
of what the caller's own lambda looks like. Measured: 80 B.

Together these two account for most of the ~270-300 B floor
`InstantTableBenchmarks`' `*Args` variants still show even with a fully
`static`, non-capturing caller lambda - both are structural to the current
`Task<Result>` + `Channel<Action>` design, not incidental waste. See
`Docs/01-performance-principles.md`'s 2026-09-14 entry for the full decision
(accepted as the contract for now, not pursued further without a real
workload profile showing it matters).
