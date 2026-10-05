# `src/RhinoDB.Sandbox.Benchmark/Benchmarks/SubmissionOverheadBenchmarks.cs` — dev notes

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

Together these two accounted for most of the ~270-300 B floor
`InstantTableBenchmarks`' `*Args` variants still showed even with a fully
`static`, non-capturing caller lambda - both were structural to the *old*
`Task<Result>` + `Channel<Action>` design, not incidental waste. **Superseded
the same day** by the pooled `ValueTask`/`IValueTaskSource` redesign
(`Docs/Dev/RhinoDB.Lib/Execution/PooledOperation.md`) - both numbers stay
here as the historical baseline the redesign was measured against, not
deleted once the question was answered.

## `PooledRunNoOp`

Added 2026-09-14, the equivalent isolation benchmark for the *new*
mechanism - a real `db.Run(static (ctx, tx) => Result.Ok(), ...)` against a
freshly-constructed `InstantBenchDb`, exercising the genuine end-to-end path
(cross-thread channel write, pooled rent, actual await, no table work)
rather than a synthetic construct - deliberately not built via direct access
to the `internal` `PooledOperation`/`Channel<IExecutionWorkItem<TTx>>` types
(which would need a new `InternalsVisibleTo` grant this project doesn't
otherwise need), since the real public `Run` path already exercises the
exact same machinery a genuine caller would. Measured: **80 B** - lower than
`BareTaskCompletionSource`'s 88 B alone, i.e. the *entire* new mechanism
costs less than just one component of the old one did. The residual is very
likely `ExecutionContext` flow across the cross-thread continuation (a cost
intrinsic to any cross-thread `await`, not specific to this design) - not
decomposed further without real profiling tools (ETW/`dotnet-trace`), same
"don't optimize blind" gate this project applies consistently elsewhere.
