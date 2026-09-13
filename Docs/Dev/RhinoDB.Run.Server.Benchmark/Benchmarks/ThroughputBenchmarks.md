# `src/RhinoDB.Run.Server.Benchmark/Benchmarks/ThroughputBenchmarks.cs` — dev notes

Added 2026-09-14, prompted by a comparison question against another
high-performance database's claimed ~300,000 transactions/second. Every
other benchmark in this project
(`InstantTableBenchmarks`, `PersistentTableBenchmarks`) measures **latency** -
the time for one isolated request, submitted alone, waiting for its own
answer - dominated by the fixed cost of crossing into RhinoDB's single-writer
thread (channel wake-up, context switch, thread scheduling) exactly once per
request. A "transactions/second" claim is a **throughput** number - items
completed per second when many concurrent submitters keep the writer queue
continuously full, so that fixed per-request wake-up cost gets amortized
across a saturated queue instead of paid fresh by every isolated call. These
are genuinely different things, not two ways of stating the same number -
this file measures the second one, which nothing else in this project did
before.

Mirrors `ConfirmedCoalescingBenchmarks`' concurrent-fan-out shape (fire N
`Run` calls in a tight loop without wrapping each in `Task.Run`, then
`Task.WhenAll` them) at a much larger `ConcurrentOperations` (10,000, vs. 16)
to get a meaningful saturated-queue reading rather than a small-batch
latency measurement in disguise. Deliberately **Optimistic-only for
Persistent** - `Confirmed`'s fsync cost is a separate, already-measured
bottleneck (`ConfirmedCoalescingBenchmarks`); mixing it in here would answer
a different question than "how fast can this engine process work."

## Real measured results (2026-09-14, `--job short`, 10,000 concurrent operations/iteration)

| Method | Mean (10k ops) | Throughput |
|---|---|---|
| `InstantConcurrentInserts` | 3.455 ms | **~2.9M ops/sec** |
| `InstantConcurrentUpdates` | 3.626 ms | **~2.76M ops/sec** |
| `PersistentOptimisticConcurrentInserts` | 10,367.6 ms | ~964 ops/sec |
| `PersistentOptimisticConcurrentUpdates` | 480.5 ms | ~20,800 ops/sec |

**Instant tables under sustained concurrent load are ~9-10x above the
300k/sec comparison point**, not behind it - confirming the "far behind"
premise the comparison started from didn't survive an apples-to-apples
measurement; the ~3μs single-request *latency* number (unaffected by this
finding, still real) was never the right number to compare against a
throughput claim in the first place. **Not yet a fully fair comparison,
though** (checked directly against the other database's own published
benchmark methodology): their ~300k/303,920 figure is a full end-to-end
measurement - real network/client stack (a TypeScript client, 64 concurrent
connections, pipelined up to 40 requests each), a multi-row balance-transfer
transaction with validation, on a dedicated 24-vCPU machine. This benchmark is a bare
in-process `db.Run()` call with no network layer at all (Stage 7+8 isn't
built yet) and a single-row write - directionally informative, not yet
like-for-like.

**`PersistentOptimisticConcurrentInserts`' large gap (~964/sec) is the same,
already-documented fresh-page-write cost** from the 2026-09-13 investigation
(`Docs/03-roadmap.md`, `Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md`) - each
concurrent insert here writes a brand-new, never-before-touched key/page,
which measured ~10x more expensive than an already-settled one on this
Windows dev machine. This benchmark is itself part of what disproved the
original Windows-Defender hypothesis (2026-09-14): re-run with a real
Defender exclusion on the whole benchmark temp folder in place, and the
number didn't meaningfully change (10,367.6 ms → 9,464.4 ms for the same
10,000-op batch). Root cause still open; still very likely specific to this
Windows dev machine either way, not the Linux servers this project targets
in production.
`PersistentOptimisticConcurrentUpdates` (writing the same already-warmed key
repeatedly) shows no such gap, matching that finding exactly - it's the
"never-before-written page" cost showing up again in a new (throughput)
measurement, not a new problem.

**A real, worth-naming caveat about this benchmark's own allocation
numbers** (2.7-4.2 MB per 10,000-operation batch, i.e. ~270-420 B/op,
noticeably above `SubmissionOverheadBenchmarks.PooledRunNoOp`'s 80 B):
`Task.WhenAll` requires real `Task`s, so every concurrent call here does
`.AsTask()` on the pooled `ValueTask` - paying the real `Task` allocation
this whole redesign otherwise avoids. This is the documented,
intentional cost of *this specific calling pattern* (genuine fan-out via
`Task.WhenAll`), not a regression in the underlying mechanism - a caller
awaiting each call individually (the common case) never pays it. Confirms
`Docs/01-performance-principles.md`'s own framing: `ValueTask` gives the
full allocation win for the dominant single-await shape, and fan-out costs
`.AsTask()` explicitly, exactly where it's actually needed.
