# `src/RhinoDB.Sandbox.Benchmark/Benchmarks/ThroughputBenchmarks.cs` — dev notes

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

## Original measured results (2026-09-14, `--job short`, 10,000 concurrent operations/iteration) — superseded below

| Method | Mean (10k ops) | Throughput |
|---|---|---|
| `InstantConcurrentInserts` | 3.455 ms | **~2.9M ops/sec** |
| `InstantConcurrentUpdates` | 3.626 ms | **~2.76M ops/sec** |
| `PersistentOptimisticConcurrentInserts` | 10,367.6 ms | ~964 ops/sec |
| `PersistentOptimisticConcurrentUpdates` | 480.5 ms | ~20,800 ops/sec |

These numbers predate the WAL (`Docs/05-wal-design.md`) replacing libmdbx's
direct synchronous commit as the write-durability path — `Persistent`'s
huge gap below was libmdbx's own mmap/copy-on-write fresh-page-write cost,
paid inline on every write. Kept here for the historical before/after
comparison, not as current numbers — see the re-measurement below.

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

**`PersistentOptimisticConcurrentInserts`' large gap (~964/sec) was the same,
already-documented fresh-page-write cost** from the 2026-09-13 investigation
(`Docs/03-roadmap.md`, `Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md`) - each
concurrent insert wrote a brand-new, never-before-touched key/page,
which measured ~10x more expensive than an already-settled one on this
Windows dev machine. This benchmark was itself part of what disproved the
original Windows-Defender hypothesis (2026-09-14): re-run with a real
Defender exclusion on the whole benchmark temp folder in place, and the
number didn't meaningfully change (10,367.6 ms → 9,464.4 ms for the same
10,000-op batch). `PersistentOptimisticConcurrentUpdates` (writing the same
already-warmed key repeatedly) showed no such gap, matching that finding
exactly - it was the "never-before-written page" cost showing up again in a
new (throughput) measurement, not a new problem. The WAL redesign
(`RawFileIoBenchmarks.cs`: 380.9μs vs 376.4μs, statistically
indistinguishable) was built specifically because libmdbx's mmap COW path was
identified as the wrong I/O shape for this — the re-measurement below is the
direct confirmation that diagnosis was correct.

## Re-measured after the WAL replaced libmdbx as the write path (2026-09-20, `--job short`, 10,000 concurrent operations/iteration)

`PropagationMode.Optimistic` writes to a `Persistent` table now append to the
WAL's in-memory staging buffer (`ColdStore.Stage`, sequential, no mmap
involved) instead of committing directly into libmdbx — libmdbx only sees
writes again at checkpoint time, off the hot path entirely (`Docs/05-wal-design.md`
Phase 2/3). This benchmark exercises exactly the write path that redesign
targeted, so it's the direct before/after proof of whether it worked.

| Method | Mean (10k ops) | Throughput | Allocated | vs. pre-WAL |
|---|---|---|---|---|
| `InstantConcurrentInserts` | 3.589 ms | ~2.79M ops/sec | 2.29 MB | ~unchanged (not on the WAL path) |
| `InstantConcurrentUpdates` | 3.317 ms | ~3.02M ops/sec | 2.21 MB | ~unchanged (not on the WAL path) |
| `PersistentOptimisticConcurrentInserts` | 9.089 ms | **~1.10M ops/sec** | 10.21 MB | **~1,140x faster** (was ~964 ops/sec) |
| `PersistentOptimisticConcurrentUpdates` | 8.411 ms | **~1.19M ops/sec** | 6.56 MB | **~57x faster** (was ~20,800 ops/sec) |

**The fresh-page-write penalty is gone, exactly as predicted.** Pre-WAL,
`Insert` (always a brand-new key/page) was ~22x slower than `Update` (an
already-settled key) - the signature of libmdbx's mmap/COW fresh-page cost.
Post-WAL, `Insert` and `Update` are now within ~8% of each other
(9.089 ms vs 8.411 ms) - both go through the same sequential WAL append
regardless of which key they touch, so there is no "fresh page" for a
sequential append to be slow on. This matches `RawFileIoBenchmarks.cs`'s
own finding (plain `FileStream` writes show no fresh-vs-settled-page gap at
all) being reproduced end-to-end through the real engine, not just in
isolation.

**Persistent throughput now clears the ~300k/sec external comparison point
too, not just Instant.** `PersistentOptimisticConcurrentInserts`/`Updates`
(~1.10-1.19M ops/sec) sit at roughly 3.5-4x that figure - previously only
the non-durable `Instant` tables cleared it (~9-10x above), while durable
`Persistent` writes were ~300x *below* it (964 ops/sec). The WAL redesign
closed that gap entirely for the throughput dimension: Persistent is now
within ~2.5-2.7x of Instant's own throughput, down from being ~3,000x
slower. The same "not yet a fully fair comparison" caveat above still
applies (no network layer, single-row writes) - this is a claim about
RhinoDB's own before/after, not a revised claim about beating the external
number under matched conditions.

**Allocation went up, not down, and that's expected, not a regression**:
10.21 MB / 6.56 MB per 10,000-op batch (~1.0-1.05 KB/op) vs. the pre-WAL
run's un-recorded-but-implied-smaller figures - `Confirmed`/`Optimistic`
writes now build a `WalChange`, stage it, and (for the group that triggers a
flush) encode a WAL frame per operation, work that simply didn't exist when
the write went straight into an already-open mdbx transaction. This is the
real, accepted cost of owning a self-serialized durability log instead of
delegating entirely to libmdbx's B+tree commit - traded for the ~1,000x
latency win above, an unambiguously good trade for a write-heavy workload.

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
