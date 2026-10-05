# ChildActivationBenchmarks (`ChildColdActivationBenchmarks`, `ChildLifecycleBenchmarks`)

What a Child costs to bring up, use and tear down, through a real `RhinoHost` (`BenchHost`, keyed
`MatchDb`, one Persistent table).

- `ChildColdActivationBenchmarks.ActivateExistingChild` — a Child already on disk with `Rows` rows,
  activated after a host restart: `ColdStore.Open`, WAL recovery, auto-load of its Persistent tables
  (`LoadFromColdAsync`), `OnStart`. `[InvocationCount(1)]` + `[IterationSetup]` restart the host every
  iteration, because the only other way to deactivate a Child (`DisposeChildAsync`) deletes it.
- `ChildLifecycleBenchmarks.LookupActiveChild` — `GetOrActivateChildAsync` on an already-active key.
- `ChildLifecycleBenchmarks.BeginTxOnActiveChild` — one awaited empty `BeginTx(key, ...)`.
- `ChildLifecycleBenchmarks.CreateUseAndDisposeChild` — a brand-new key: activate (create dir, open
  store, create WAL), one insert, dispose (drain, settle, delete dir).

## 2026-10-05 (Windows 11, .NET SDK 11.0.100-rc.1.26425.128, BenchmarkDotNet 0.14.0)

| Benchmark | Rows | Mean | Allocated |
|---|---|---|---|
| `ActivateExistingChild` | 0 | 2.53 ms | 77 KB |
| `ActivateExistingChild` | 1,000 | 2.41 ms | 474 KB |
| `ActivateExistingChild` | 100,000 | 31.0 ms (median 35.2, StdDev 10.4 — bimodal) | 42.4 MB |
| `LookupActiveChild` | — | 25 ns | 96 B |
| `BeginTxOnActiveChild` | — | 3.21 µs | 266 B |
| `CreateUseAndDisposeChild` | — | 6.10 ms | 86 KB |

**Activation has a ~2.5 ms fixed cost** (libmdbx environment + WAL open, recovery check), so up to a few
thousand rows the auto-load is invisible. **At 100k rows the load dominates: ~0.3 µs and ~430 B per row.**
The allocation is `BulkLoadFromCold`'s intermediate `List<TRow>` plus the scan's per-row deserialization;
loading straight into storage would cut most of it if large Children become common. The bimodal timing
at 100k is Gen2 GC landing in some iterations and not others.

**First-touch latency is the real cost for players:** the first request to a dormant Child pays the
activation (2.5 ms small, ~30 ms at 100k rows); later requests pay only the 25 ns lookup. `LookupActiveChild`'s
96 B is the `async` method's `Task` - a cached completed task or a `ValueTask` fast path would remove it.

`BeginTxOnActiveChild` (3.2 µs, a single awaited round trip) matches
`SubmissionOverheadBenchmarks.PooledRunNoOp` (3.07 µs): routing to a Child adds nothing measurable over
running on the Root - the time is the cross-thread wake of one serial await, not the Child.

`CreateUseAndDisposeChild` (~6 ms) is directory creation + store/WAL creation with their directory
fsyncs + one write + drain + recursive delete - fine per match/session, not something to do per request.
