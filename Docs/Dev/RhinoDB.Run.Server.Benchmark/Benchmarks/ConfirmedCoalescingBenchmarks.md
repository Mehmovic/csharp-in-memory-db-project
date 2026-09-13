# `src/RhinoDB.Run.Server.Benchmark/Benchmarks/ConfirmedCoalescingBenchmarks.cs` — dev notes

Added 2026-09-13 to measure `ColdStore`'s commit-coalescing window
(`Docs/02-architecture.md` § "Confirmed commit batching"). See that section
and `Docs/03-roadmap.md`'s 2026-09-13 entry for the full result - summary
below.

**2026-09-14**: `Setup`'s two seeding loops needed `.AsTask()` before
`.GetAwaiter().GetResult()`, and both `[Benchmark]` methods needed `.AsTask()`
inside their `Task[]` fan-out loops, once `Run` switched to returning
`ValueTask<Result>` - see `Docs/Dev/RhinoDB.Lib/Execution/PooledOperation.md`
for why (`ValueTask` doesn't support blocking synchronous wait, and
`Task.WhenAll` needs real `Task`s). This project is itself the concrete proof
that the new `ValueTask`-returning API still supports genuine concurrent
fan-out at a one-line-per-site cost, exactly the scenario it was designed to
keep supporting.

## `ConcurrentConfirmedInserts`

Fires several `Confirmed` writes back-to-back without awaiting each one
before issuing the next - the shape a real multi-connection server produces
(many independent callers wanting a durability guarantee around the same
time). This is the *only* pattern a coalescing window can help with: a
purely serial caller (await each `Confirmed` call before issuing the next,
see `PersistentTableBenchmarks.InsertConfirmed`) never has a second commit
to piggyback with, so the window only ever adds its own delay there, never
removes one.

Each writer here inserts a brand-new key, so this benchmark is dominated by
the fresh-page-write cost from the 2026-09-13 latency investigation
(~900 μs-1 ms per commit, independent of `Confirmed`/`Optimistic` or fsync
scheduling) - that cost happens synchronously inside `Commit()`, before any
coalescing logic runs at all, so it swamps whatever fsync savings coalescing
could offer. Measured: ~15 ms for 16 concurrent inserts regardless of window
value, i.e. no visible coalescing effect either way here - not because
coalescing doesn't work, but because this scenario isn't fsync-bound.

## `ConcurrentConfirmedUpdates`

Isolates the fsync-coalescing question from that fresh-page-write cost:
every writer here updates the *same* already-warmed, already-settled key
(`lookupKey`, seeded during `Setup`), so each individual `Commit()` is cheap
(~50-130 μs, per `PersistentTableBenchmarks.Update`) and whatever's left
over is a much fairer test of whether batching the fsync itself saves
wall-clock time.

**Real finding**: `CommitCoalescingWindowMs` values in the architecture
doc's originally-suggested 0-2 ms range are not achievable via plain
`Task.Delay` on Windows - a direct probe (`Task.Delay(1)` through
`Task.Delay(5)`) measured actual elapsed times of 9-15 ms, matching
Windows' well-known ~15.6 ms default system timer tick. Any nonzero window
in that range therefore adds a flat ~15 ms tax per batch on this platform,
regardless of the requested value - measured at window=1/2/5 ms, all landed
at ~15.5 ms (`ConcurrentConfirmedUpdates` at window=0 was ~1.6-1.8 ms by
comparison). Confirmed this is a *platform granularity floor*, not a bug in
the coalescing mechanism itself, by testing at window=20 ms (above the
~15.6 ms tick) - result was ~31 ms (≈2 ticks, matching the same probe's
`Task.Delay(16)` ≈ 30.5 ms), and critically *not* ~1.6 ms (16 × the
~100 μs per-commit cost) - proving all 16 concurrent updates really did
share exactly one fsync call rather than firing independently, exactly as
`ColdStoreTests.cs`'s `EndScope_WithCoalescingWindow_*` unit tests already
proved in isolation.

**Practical conclusion**: don't enable this window on Windows without first
adding a higher-resolution wait (e.g. P/Invoke `timeBeginPeriod(1)`, or a
spin-wait for sub-tick windows) - as it stands, the ~15.6 ms granularity
floor vastly exceeds the per-commit cost it's trying to save (tens to
low-hundreds of μs), making it a net loss at any Windows-achievable window
size. `ColdStore`'s default (`commitCoalescingWindow: default` = `TimeSpan
.Zero`) stays off for exactly this reason. Untested whether Linux's
generally finer-grained kernel timers behave differently - worth revisiting
specifically there if sustained concurrent-`Confirmed` throughput ever
becomes a real bottleneck in production, not before.

`[Params(0, 20)]` reflects this: `0` is the realistic (default, off)
baseline, `20` is chosen specifically to land above the ~15.6 ms tick so the
result demonstrates the mechanism actually batching rather than being
dominated by rounding noise - values in between are not benchmarked since
they demonstrably can't hit their requested target on this platform.
