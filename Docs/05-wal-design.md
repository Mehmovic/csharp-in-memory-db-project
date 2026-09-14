# WAL durability — zero-to-hero plan (WAL + libmdbx checkpoint + ring-buffer catch-up)

> **DECIDED — 2026-09-14. Phase 0 gate passed; this is now committed design.**
> Deciding to build this changes `Confirmed`'s meaning, the restart story, and
> adds the project's first self-owned correctness surface. Phase 0's
> prototype (`WalPrototypeBenchmarks.cs`) cleared all three go/no-go numbers
> decisively — see the Phase 0 section below for the real measurements.
> Building (Phases 1-4) is now in progress; this banner tracks design status,
> not implementation-complete status.

## The shift (what and why)

Today: in-memory state + libmdbx is *both* the durability mechanism (every
`Confirmed` call fsyncs a B+tree commit) and the cold-storage tier (load/
evict/peek).

Proposed split, three tiers of the same data:

1. **In-memory** (`DenseArray` + indexes) — the working set. Unchanged.
2. **WAL (new, self-owned)** — append-only log of committed `Change`s; the
   durability mechanism. A `Confirmed` call is durable when its entry is
   fsync'd here, not when libmdbx has it.
3. **libmdbx** — demoted to *checkpoint store* (periodic snapshot of settled
   state; WAL truncated after checkpoint) while **keeping its current role
   unchanged** as the cold-storage tier (load/evict/peek, eager-load,
   `Peek` random durable reads).

Why this is worth considering, on evidence already gathered:

- **B+tree commit is the wrong I/O shape for per-txn fsync.** The measured
  ~900μs–1ms fresh-page-write cost and ~50–130μs settled `Commit()` cost are
  copy-on-write page taxes: one logical commit touches scattered pages. A WAL
  append touches one place, sequentially — the cheapest possible durable I/O.
- **Group commit works here without the timer-tick trap.** The 2026-09-13
  coalescing-window experiment failed on Windows because piggybacking needed a
  `Task.Delay`-based window (15.6ms tick floor). WAL group commit needs no
  window at all: a `Confirmed` arriving while a prior fsync is in flight
  *naturally* joins it. Same idea, but the underlying I/O shape makes it win.
- **One log, two consumers.** The WAL's change entries are also the reconnect/
  catch-up stream (with a RAM ring buffer as the short-horizon front of it —
  see Ring buffer below), replacing the "in-memory ring of recent changes"
  catch-up idea that Stage 8 would otherwise have needed anyway.
- **Bounded startup**: open checkpoint + replay WAL tail, vs. eager-loading
  whole tables.

Precedents: this is the classical shape (Postgres: WAL + checkpoint;
SpacetimeDB: commitlog + periodic snapshot), with one deliberate difference —
libmdbx stays as the random-read cold tier, which a pure commitlog design
(SpacetimeDB) does not have and cannot grow into.

### The data flow, and where the engine waits (clarified 2026-09-14)

One-directional: **memory → WAL (durable) → mdbx (settled)**, with exactly one
forced-drain point. The full picture:

- **`Confirmed` waits only on WAL group fsync** — sequential append,
  group-committed. Never on libmdbx.
- **`Optimistic` waits on nothing** — its change is durable by the next group
  fsync whenever that fires. Semantics unchanged from today.
- **libmdbx is written in the future, in bulk, off the latency path** — at
  checkpoint time only. The measured B+tree per-commit costs (~1ms fresh
  pages, ~50–130μs settled) stop being per-commit taxes and become amortized
  checkpoint cost.
- **libmdbx is read-only on the hot path** — `Load`/`Peek` never wait and
  never block on anything but their own single keyed read (see `Load`/`Peek`
  final semantics in Phase 3 — the ring is *not* involved).
- **The one forced drain point: eviction write-through — batched (refined
  2026-09-14).** Evicting rows first writes their current in-memory values
  into their mdbx sub-databases, then drops them from memory — staged like
  changes and applied as **one batch (one mdbx txn, one fsync)**, not per-row
  (details and crash rules in Phase 3). This is what makes `Load`'s no-wait
  safety possible: the invariant it rests on is "there is never
  newer-in-WAL-only state for an evicted row" — eviction itself puts the row's
  exact current value into mdbx, so "pending in the WAL" never matters for a
  non-resident row.
- **Eviction and checkpointing cooperate**: because eviction implies "this row
  is fully settled in mdbx," checkpoints may skip evicted rows outright —
  high-eviction workloads effectively checkpoint themselves incrementally.

### Benefits of keeping libmdbx as the checkpoint/cold tier

- **The durable tier is independently queryable at rest.** Because the on-disk
  state is (checkpoint + WAL tail) and the checkpoint is a keyed store, a
  *stopped* database is queryable with tooling — no game process, no engine:
  - **Checkpoint-only queries**: open the mdbx file read-only (multi-reader)
    and cursor-scan any table — stale only by the un-checkpointed tail.
  - **Exact-as-of-last-durable-write**: open the checkpoint, replay the WAL
    tail in memory through the generated `Apply` machinery (bounded: tail
    only). Cheap enough to make an operational tool (`rhinodb inspect`)
    realistic.
  - The WAL itself is linearly queryable — "every change between LSN X and Y"
    is a log scan, a free audit/debug primitive.
  This is a real advantage over SpacetimeDB's shape, whose data at rest
  (snapshot image + commitlog) exists only for the engine to reload — row-level
  offline access isn't something it offers. Corollary commitment: from the day
  the WAL ships, the on-disk formats (mdbx sub-db layout + WAL entry format)
  are **stable surface**, same discipline as the generated `Accessor` names —
  file-header versioning (Phase 1) and the own-both-ends codec rule are what
  keep this promise tractable.
- **Cold reads stay random-access**: `Peek(id)` on an evicted row of a 50M-row
  table, mid-flight, is a core cold-tier feature — the job libmdbx is best at.
- **The dependency's weakness is shed by the shift itself**: libmdbx's only
  measured pain (per-commit CoW page writes) disappears structurally once it
  writes only at checkpoint time. What remains are its strengths (crash-safe
  random reads, multi-reader, mmap page cache, page-level checksums).

### Alternatives considered: a SpacetimeDB-style self-owned snapshot

SpacetimeDB needs no keyed durable snapshot because it *never reads its
snapshot randomly* — restart loads the whole image sequentially into RAM and
replays on top; every read is in-memory; disk is never queryable. Their
"checkpoint" can therefore be a dumb sequential image.

That route is open to RhinoDB only by giving something up: a sequential image
can't serve `Peek`'s by-key mid-flight reads without adding an on-disk index,
page-level random access, and a crash-ordering protocol — i.e., rebuilding a
worse libmdbx in parallel with already owning a battle-tested one. The honest
version of the alternative is a **product** decision, not an implementation
one: if cold reads were ever re-scoped to "always replay to RAM, dataset
always fits in memory," then `Evict`/`Peek`/the cold tier become dead weight to
*delete*, not a file format to swap. Decided 2026-09-14, pending Phase 0: keep
libmdbx; revisit only if the cold tier's purpose is consciously re-scoped.

## What we need to build — the phases

### Phase 0 — prove it before building it (the gate)

Standalone prototype WAL: append frames of serialized `Change`s, group-commit
fsync, **no libmdbx involvement at all**. Benchmark under the exact
`ConfirmedCoalescingBenchmarks` workload shapes (fresh-key inserts; settled-key
updates; 16 concurrent `Confirmed` calls). Go/no-go numbers to beat:

- fresh-key batch cost clearly under the current ~1ms/fresh-page path;
- 16 concurrent `Confirmed` updates well under the current ~15ms, with no
  `Task.Delay` in the mechanism at all;
- steady-state per-op `Confirmed` cost lower than the settled ~50–130μs B+tree
  commit (the append+fsync share of a group should be single-digit μs).

If the prototype doesn't beat these, the shift is dead on arrival — that is a
successful experiment too, recorded and closed like the coalescing one was.

**Result (2026-09-14, `WalPrototypeBenchmarks.cs`, `--job short`) — clears all three:**

| Benchmark | Mean | Per-op |
|---|---|---|
| `BatchFreshKeyConfirmedInserts` (10,000 concurrent, fresh keys) | 71.535 ms | **7.15 μs/op** |
| `ConcurrentConfirmedFreshKeyInserts` (16 concurrent, fresh keys) | 446.7 μs | 27.9 μs/op |
| `ConcurrentConfirmedSettledKeyUpdates` (16 concurrent, settled key) | 447.7 μs | 28.0 μs/op |
| `SingleConfirmedSettledKeyUpdate` (1 isolated call, no group to join) | 420.1 μs | — |

- **Go/no-go #1** (fresh-key batch clearly under ~1ms/fresh-page): the current
  mdbx path measures ~1,036.8 μs/op (`ThroughputBenchmarks.PersistentOptimisticConcurrentInserts`,
  10,367.6 ms / 10,000). The prototype's 7.15 μs/op is **~145x faster**. Clears
  decisively.
- **Go/no-go #2** (16 concurrent Confirmed well under ~15ms, no `Task.Delay`):
  446.7-447.7 μs total for the whole 16-way batch, **~33x under** the 15ms
  bar, with the group-commit mechanism containing no timer/delay of any kind
  — pure piggyback-on-in-flight. Clears decisively.
- **Go/no-go #3** (steady-state per-op below the settled ~50-130 μs mdbx
  commit, single-digit μs group share): 27.9-28.0 μs/op at 16-way concurrency
  already clears the 50-130 μs bar; at 10,000-way concurrency the per-op share
  drops to 7.15 μs — genuinely single-digit, exactly the parenthetical's
  expectation, and it drops further as concurrency rises (more callers share
  one fsync), the opposite of mdbx's per-commit COW tax. Clears decisively.
- **`SingleConfirmedSettledKeyUpdate`'s 420.1 μs is not a go/no-go number** —
  a single, completely isolated Confirmed call has no one else's fsync to
  join, so it pays one real `Flush(flushToDisk: true)` round-trip no matter
  the mechanism. This is expected physics, not a flaw, and it usefully
  cross-validates `RawFileIoBenchmarks.cs`'s independently-measured raw fsync
  cost (380.9/376.4 μs) — same machine, same order of magnitude, different
  benchmark. Real traffic is never this pattern (the whole point of the
  execution loop is to keep the writer busy), so the concurrent numbers above
  are what actually matters.

**Decision: go.** Proceeding to Phase 1.

### Phase 1 — WAL file + entry format

- One file per database (per `DbContext`), append-only; **no segment rotation
  in v1** — one file, truncated at checkpoint (segments are the source of the
  directory-fsync bug class; v1 only ever creates the file once, and still
  fsyncs its directory after first create — reuse
  `RhinoDB.Native.DirectorySync`).
- Entry layout (fixed-width header, MemoryPack payload — the *cold storage*
  codec, not the client-access one, since the WAL is same-process same-version,
  like IDC):
  `[u32 length][u32 checksum][u64 lsn][byte kind][payload]` — kind ∈
  `Insert/Update/Delete/CheckpointMarker`. Delete carries the key only.
- **Entries carry full row values, not field deltas — decided.** The rule:
  *one entry per operation; the entry lists only the rows the operation
  touched; each touched row is recorded as its complete new value (or a
  key-only tombstone).* Scope comes from the operation (only touched rows),
  completeness comes from the update model (`Update(TKey, TRow newRow)` already
  produces complete replacement rows — the WAL payload is the serialized form
  of what `Apply()` consumes today). Field-level delta encoding is rejected:
  it makes replay state-dependent (deltas against intermediate states), breaks
  on every row-shape change, and saves tens of bytes on small structs — a
  non-cost. Whole-data snapshot entries are equally rejected for the obvious
  reason; "whole data" belongs to the checkpoint, exactly once, not per-entry.
  Consequence worth keeping: full-value entries make replay stateless and
  idempotent, and make ring/catch-up entries self-contained for clients.
- **Checksum every entry** (CRC32C-class). The #1 crash-corruption defense: a
  torn/partial tail entry is detected by checksum and dropped — replay stops
  at the first bad entry and treats the tail as never-committed (must match
  the rule that only the fsync'd prefix is real).
- File header at position 0: format version + database identity. On mismatch,
  refuse to open — fail loudly, never guess.

### Phase 2 — group commit (the durability engine)

- Writer path: a `Confirmed` call appends its entry to an in-memory staging
  buffer (on the writer thread — the append is in-memory, fast); a **single
  in-flight fsync task** per database. A `Confirmed` arriving while one is in
  flight joins the current group; when the fsync completes, *every* `Task` in
  the group completes. No window, no timer, no delay — piggyback-on-inflight
  only. This replaces `ColdStore.EndScope`'s sync-firing branch as the
  `Confirmed` gate.
- `Optimistic` calls append to the staging buffer too but **do not wait** —
  their changes reach durability by the next group's fsync (triggered by: a
  `Confirmed` arrival, a buffer-size threshold, or a periodic tick — whichever
  first). Their semantics are unchanged: no durability promise before return.
- **Group integrity rule**: an fsync'd group must be a contiguous prefix of
  the logical change sequence — no gaps, no reordering. LSNs are assigned on
  the writer thread, which is already the serialization point, so this comes
  free from the single-writer model.
- Crash rule: only the fsync'd prefix exists; everything past it is treated as
  never happened.

**Sustained-load validation (2026-09-14, `WriteAheadLogSustainedLoadTests.cs`)** — Phase 0's
numbers were all single-burst (fire N concurrently, wait for all, done). A first attempt at a
sustained-load check used 16 workers each awaiting their own append before issuing their next one
— that measured **~280 μs/op, ~10x worse than Phase 0's burst numbers**, alarming until the model
itself was recognized as wrong: nothing in the real system ever calls `AppendConfirmed` that way.
`PooledOperation.Run()` never awaits one operation's durability before `DbExecutionLoop.RunLoop`
dequeues the next — the single writer thread dispatches back-to-back, non-blocking, which is much
closer to Phase 0's burst shape than to a self-throttling request-response loop. Corrected to 10
successive bursts of 5,000 (fired without individually awaiting each one, matching the real
dispatch pattern) after a 20,000-op untimed warm-up: **15-40 μs/op steady state**, no degradation
across the run (second-half average lower than first-half). Confirms group commit holds up over a
longer sustained run, not just a single burst — and is a reminder that a benchmark's *concurrency
shape*, not just its op count, has to match how the real caller actually behaves, or the number it
produces is meaningless (worse: alarming in the wrong direction).

**Correctness review findings (2026-09-14) — two real bugs, both fixed:**

1. **Late-joiner race (durability-breaking).** The original `RunFlush` released `appendLock` right
   after `Flush()` returned, then cleared `inFlightGroup` afterward under the *separate* `groupLock`.
   An append landing in that gap would write bytes not covered by the flush that just happened, yet
   still observe the stale `inFlightGroup` and get told it was durable when it wasn't. Fixed by
   holding `appendLock` across the *whole* flush-then-clear sequence (`groupLock` nested inside it) —
   no append can write and no group can be joined in the gap anymore. Regression test:
   `AppendConfirmed_ManyConcurrentAppendsUnderLoad_EveryAcknowledgedEntryIsActuallyPersisted`
   (`WriteAheadLogTests.cs`) — fires 5,000 concurrent appends and verifies every one acknowledged as
   durable is actually present on disk afterward.
2. **`Truncate` stranded the write position (corruption on next append).** `FileStream.SetLength`
   doesn't move `Position`; nothing called `Seek` afterward, so the next `Write` landed at the stale
   pre-truncate offset — a sparse hole instead of a clean WAL. Fixed with an explicit `Seek` to
   `WalFileHeaderCodec.Size` right after `SetLength`. Regression test:
   `Truncate_ThenAppend_WritesRightAfterTheHeaderNotAtTheStalePosition`.

Also widened `RunFlush`'s catch from `IOException` to `Exception` (an `ObjectDisposedException` from
a dispose racing a still-scheduled flush would otherwise escape uncaught, leaving `tcs` permanently
uncompleted — a hang for every caller sharing that group, not just an unobserved exception), and made
`Dispose` wait for any in-flight group before disposing the file stream.

**A real, currently-unsolved follow-up, not pursued further today:** holding `appendLock` across the
entire fsync means any append landing mid-flush blocks the calling thread for the flush's full
duration — in production that thread is the single writer thread, which the whole pooled-execution
redesign this session started with was built specifically to keep non-blocking. A same-day attempt at
a lock-free fix (`RandomAccess.FlushToDisk` on the raw handle instead of `FileStream.Flush`, closing
group membership right before the fsync call instead of after) reasoned out correctly on paper and
matched the existing tests in a dry trace, but **crashed the test host process** when actually run —
a real bug in that approach, not yet root-caused. Reverted to the lock-spanning version (proven
correct via 5 repeated clean full-suite runs) rather than debug a process crash in an increasingly
complex design under time pressure. This gap is real and worth a dedicated pass later — with more
time, a smaller isolated repro, and probably native-level debugging — not blocking Phase 3 or the
`ColdStore` wiring next, since the current design's throughput (15-40 μs/op sustained, see above) is
already well under the mdbx cost it replaces even with this cost included.

### Phase 3 — checkpointing (WAL ↔ libmdbx integration)

- **Checkpoint LSN** stored in a small libmdbx metadata sub-database: "all
  changes ≤ LSN are fully materialized in this snapshot."
- **Checkpoint procedure — revised 2026-09-14 during implementation, two
  changes from the original framing above, both tightening it:**
  1. freeze the current group's boundary LSN;
  2. **one single mdbx write transaction** does *both* the row dump *and* the
     watermark record — not two separate steps. Rationale: the original
     "fsync mdbx, then atomically record the checkpoint LSN" framing read as
     two transactions, which would reopen exactly the crash window risk #2
     names ("crash between mdbx dump and LSN record"). Folding both into one
     `Commit()` makes that crash window **impossible by construction** — the
     dumped rows and the watermark either both land or neither does. One
     fewer failure mode to test for, not a corner cut.
  3. **the transaction also deletes every key removed since the last
     checkpoint, not just puts resident rows — a gap in the original
     procedure, caught during implementation.** "Dump every in-memory table's
     settled rows" only covers rows still resident; a row that was deleted is
     by definition no longer resident, so a naive resident-only dump would
     silently leave a stale copy in mdbx forever. The fix: the checkpoint
     transaction also replays every `ChangeKind.Delete` WAL entry since the
     last checkpoint as an mdbx delete — the WAL tail is the only place those
     tombstones exist before checkpoint, which is exactly why full-row-value
     WAL entries (Phase 1, above) already carry a key-only tombstone for
     deletes, ready to be consumed this way.
  4. `Commit()`, then `env_sync_ex`;
  5. fsync the WAL once more, then truncate it to zero and fsync again — the
     WAL after a checkpoint logically starts at checkpointLSN+1.
- **Restart = open mdbx + replay WAL entries > checkpoint LSN** through the
  normal generated `Apply` machinery (bounded work: only the tail since the
  last checkpoint).
- **Checkpoint frequency is policy, not mechanism** — size-based (WAL exceeds
  N) and/or time-based; both configurable, conservative defaults. Checkpoint I/O
  must never run concurrently with a group fsync on the same file — serialized
  on the writer thread.
- **Eviction rule (refined 2026-09-14, final):** the one forced-drain point in
  the whole design, and simpler than first drafted. `Apply()` already puts the
  latest value into the in-memory `DenseArray` synchronously — so the current
  in-memory row *is* always the latest value, full stop. Eviction is therefore
  not a replay of "pending WAL changes" (of which there may be many; only the
  final value matters to mdbx): it is one `cold.Put(coldTable, key,
  storage.Get(offset))` of the current value, read straight from the table's
  own storage, then drop the row from memory. No pending-writes index, no ring
  scan, no per-row LSN watermark — the ring stays purely a catch-up cache with
  no eviction-correctness obligation. The invariant is:
  **mdbx's copy of any evicted row is exactly current** — and crash windows
  are safe on both sides of the Put (before it: WAL replay restores the row
  resident; after it, before the memory drop: replay restores it with the
  identical value).
- **Eviction is batched, not per-row (decided 2026-09-14):** eviction
  candidates are staged like changes are, and applied as **one batch — one
  mdbx write txn, one commit, one fsync** for all rows in it. Rules:
  - Batch is applied on the writer thread between `Run` calls (no locks, no
    concurrency); its mdbx txn is atomic across all staged rows for free.
  - At batch-apply time each staged row is re-read from `DenseArray` — if the
    row was updated between staging and apply, the *current* value is what
    gets written (memory is authoritative at apply time, so staging races are
    moot by construction); alternatively a re-staged "hot again" row can be
    skipped — reading-at-apply is the default.
  - Crash/failure rule unchanged: batch commit fails → no rows evicted, all
    stay resident (eviction remains best-effort reclaim, never
    correctness-required); commit succeeds then crash before the memory drops
    → replay restores the rows resident with identical values.
  - The trade: rows may stay resident marginally longer (until the batch
    boundary) — a non-cost for a reclaim optimization — in exchange for one
    fsync amortized over many evictions, the same group-commit economics as
    the WAL applied to the mdbx side.
  - Bounded like every staging buffer: size cap / flush trigger (same shape as
    the WAL staging buffer's hard cap, risk #4) so an eviction burst cannot
    grow memory without limit.
- **`Load`/`Peek` (final):** memory first (resident rows are always current);
  non-resident rows are one synchronous `cold.Get` — safe with zero
  synchronization because eviction guarantees "evicted ⇒ mdbx exactly
  current." It's a synchronous wait, yes — but a cheap one (single keyed read
  through mmap, often OS page cache), paid only for non-resident rows, and it
  is the correctness price of "current durable value of this row."
  **The earlier "Peek consults the RAM ring" proposal is dropped** — the
  write-through eviction rule removed the only state that made it necessary;
  Peek never touches the WAL or the ring.
- **Merged cold+memory queries (final):** for a view/query over an
  `Evictable` table with partly-resident rows: iterate `DenseArray` for
  resident rows, then cursor-scan the cold store **skipping resident keys**.
  No conflict resolution needed because the key space is **strictly
  partitioned** — a key is either resident (memory authoritative, mdbx copy
  maybe stale) or evicted (mdbx copy exactly current), never both — so
  `memory ∪ (cold − resident keys)` is the whole table with no overlapping
  versions. The single writer thread performs both reads (already the model:
  internal reads are serialized at the same point as writes), so there is no
  concurrent-modification question either. The cold scan is a cursor walk —
  slower than the dense array, fine: that path serves query completeness, not
  the hot path.

### Phase 4 — ring buffer: reconnect catch-up without touching disk

> **Decided 2026-09-14**: the ring buffer covers **both** Instant and
> Persistent tables, not just what the WAL durably logs. This needs one new,
> deliberately accepted generated-code hook — see the amendment to risk #7
> below — since Instant tables never touch `ColdStore` and have no other way
> to reach a shared per-database structure. The WAL/durability seam itself
> (`ColdStore.Put`/`Delete`/`BeginScope`/`EndScope`) still stays exactly as
> today's generated code calls it — this exception is scoped narrowly to
> ring-buffer recording, not durability.

- **The idea**: a per-database circular buffer of the most recent committed
  `Change`s, LSN-indexed, held in RAM. Its retention horizon = the longest
  supported client disconnect (policy: e.g. 10k changes or N MB —
  configurable, like `ChunkSize`).
- **Unified recording hook (both table kinds)**: `TableGenerator.cs`'s
  `EmitPersistentApply` and `EmitInstantApply` both gain one call per applied
  change — `ring.Record(tableId, kind, lsn, key, row)` — at the exact point
  each already clears `changes`/resets `Dirty`. `{Db}`'s constructor builds
  one `ChangeRingBuffer` and passes it into every table's `Ops` instance
  regardless of kind, including databases with zero Persistent tables (no
  `ColdStore` at all). `tableId` is a compile-time FNV-1a hash of the table's
  `Accessor` string, embedded as a generated `const uint`. The LSN counter is
  shared across both consumers — a single per-`{Db}` increment, read once per
  operation on the (already-serializing) single writer thread before the
  validate/apply pass — so Persistent-table WAL entries and Instant-table
  ring-only entries share one sequence, with expected gaps in the WAL
  wherever an operation touched only Instant tables.
- **Why it works with the WAL, not instead of it, for Persistent data**:
  reconnect handling becomes a two-level lookup on the requested cursor:
  1. `cursor ≥ ring.oldestLsn` → replay from RAM. **Zero disk reads** — the
     common case (any disconnect shorter than the retention horizon).
  2. `cursor < ring.oldestLsn` → fall back to reading the WAL tail file
     sequentially from that LSN (rare, still cheaper than a snapshot resend);
     if the LSN has been checkpointed away → full snapshot resend, the same
     path as a first-time subscribe.
  For Instant-only changes there is no WAL tail to fall back to (Instant is
  deliberately non-durable) — a cursor older than the ring's retention for an
  Instant table always means a full resync, the same as a first-time
  subscribe.
- **Structure**: preallocated ring of `Change` records (or of pooled byte
  buffers holding entry bytes) — `ArrayPool`/chunk-allocated, no per-change
  heap allocation, LSN→slot is a modulo of the ring's base LSN. Written on the
  writer thread at commit (same place Stage 8's fan-out would have hooked
  anyway), read only by reconnect handlers.
- **Fan-out synergy**: Stage 8's subscription engine gets its diff source from
  the same ring for *live* delivery (fan-out reads the change at commit time
  anyway); the ring's real job is *late joiners and reconnects*, guaranteeing
  catch-up never blocks on disk I/O on the hot path.

## What to be cautious about (the real risk list)

1. **`Confirmed`'s meaning changes — write it down as a decision.** From
   "synced into libmdbx" to "fsync'd into the WAL." Every existing consumer
   that reasons about mdbx-commit-success (per-table delivery guarantees, IDC's
   Confirmed-gates-propagation rule, `ColdStoreTests`' durability assertions)
   must be re-derived against the new gate. Not drift — a dated decision entry.
2. **Checkpoint/WAL consistency is now your bug surface.** The crash matrix to
   test exhaustively (crash-injection tests, not happy paths): crash between
   mdbx dump and LSN record; between LSN record and WAL truncate; torn WAL
   write (power loss mid-append); checksum mismatch mid-file (not just tail);
   replay of an entry whose group partially fsync'd (impossible by
   construction — prove it by test). Every one of these is a bug class
   SpacetimeDB ate years of and libmdbx currently absorbs for you.
3. **Windows fsync reality**: check what `FlushAsync`/`FileStream` + fsync
   actually costs and guarantees on Windows vs Linux early in Phase 0 —
   `FILE_FLAG_WRITE_THROUGH` vs buffered append differences can silently turn
   "group commit" back into per-op fsync. Measure on both OSes before
   believing the numbers.
4. **The staging buffer must be bounded.** A pathological `Optimistic` burst
   with no fsync trigger must not grow memory without limit — the size
   threshold trigger from Phase 2 is the bound; make it a hard cap that forces
   an fsync, not a soft hint.
5. **Ring buffer sizing is a latency/memory tradeoff, not a correctness one**
   (the WAL fallback covers undersized rings) — keep the fallback path working
   *and tested* even though it's rare; untested fallback paths rot exactly
   when they're needed.
6. **Replay must be idempotent and validated-only-once.** Replayed changes
   re-apply through the same `Apply` machinery (good — atomic per call), but
   replay must use the unchecked fast path (data was validated at original
   commit) *and* tolerate rows whose in-memory state already matches (a
   `Load`-then-apply that hits an already-current row must be a no-op, same
   idempotence `LoadInternal` already guarantees).
7. **Don't touch the generated-code surface for the WAL/durability seam.** The
   WAL lives under `ColdStore`/`DbExecutionLoop`'s `EndScope` seam, the same
   containment that let the async-sync and coalescing changes land without
   perturbing generated code — `Put`/`Delete`/`BeginScope`/`EndScope`'s public
   shape and call sites are unchanged. **Amendment, decided 2026-09-14**: this
   discipline is narrowed, not abandoned — the ring buffer's unified recording
   hook (Phase 4) is a deliberate, accepted exception, since giving Instant
   tables reconnect-catch-up coverage has no other seam to hook into. If any
   *other* part of the design starts requiring generated-code changes beyond
   that one hook, that's still a smell to stop and re-derive at.
8. **Scope discipline**: this is a Stage 5.5-scale effort with Stage 7/8
   queued. Recommended order per the existing build-order logic: Phase 0
   prototype *now* (cheap, data-producing), then decide; if go, build WAL
   (Phases 1–3) as its own verified slice **before** Stage 7/8, since Stage 8's
   reconnect/catch-up and `Resume` cursor design (04-networking.md) builds on
   the LSN/ring model — better to have the real mechanism under the protocol
   than to redesign the protocol around a hypothetical one.

## How this roadmap should be used

Phase 0 first, always. No Phase 1 line of code before the prototype's numbers
exist next to the current mechanism's. Same gate as the coalescing experiment:
mechanism-correctness can be proven cheaply; *whether it helps on this
platform* is an empirical question answered only by running both.

## Open design questions (2026-09-14 review) — resolved 2026-09-14

1. **LSN granularity — resolved: the operation is the atomic log unit.** One
   LSN per `Run` call (assigned once, before the validate/apply pass), one
   WAL entry per operation carrying **all** of that operation's Persistent
   `Change`s across every dirty table (not one entry per individual change),
   one ring slot per operation carrying all of it (Instant and Persistent
   alike). This is why: it matches the existing cross-table atomicity model
   (validate-all-then-apply-all already treats one operation as one unit —
   `Milestone2Tests.CrossTableAtomicity_...`), it means resume cursors always
   land on operation boundaries (never a torn op mid-replay), and the
   Phase 2 "contiguous prefix, no gaps" integrity rule becomes exactly "no
   gaps between *operation* LSNs" in the full LSN sequence — with
   Instant-only operations appearing as gaps in the WAL's own (durable-only)
   view, exactly as Phase 4 already described, not a second contradicting
   rule. Phase 1's entry layout is amended: the payload is an array of
   `(tableId, kind, key, row)` tuples for that operation's Persistent changes,
   not a single change.
2. **`tableId` collision policy — resolved: a generator diagnostic.** FNV-1a
   of `Accessor` at 32 bits is fine given `RHINO010` already enforces
   `Accessor` uniqueness within a database. The generator computes every
   table's `tableId` at build time (already has every `Accessor` string in
   scope); if two distinct tables in the same database hash-collide, that's a
   new build-time diagnostic (same category as `RHINO010`), not a runtime
   concern — decided non-issue, not an archaeology question later.
3. **Phase 0 doesn't exercise the ring hook — resolved: an explicit
   obligation on the phase that adds it.** Phase 0's prototype is WAL-only,
   no libmdbx, no generator, so it never touches
   `EmitPersistentApply`/`EmitInstantApply`. Whichever build stage wires the
   ring-buffer recording hook into the generator (Phase 4) owns its own test
   pass proving *both* recording call sites actually fire, including an
   Instant-only database (zero Persistent tables, no `ColdStore`) — this is
   already reflected in the execution plan's step 6.
4. **Eviction write-through's data source — resolved: read the in-memory row
   directly, no ring/WAL lookup at all.** Every write already applies to the
   in-memory `DenseArray` synchronously as part of `Apply()`, before/alongside
   the WAL append — so the current in-memory row *is* always the latest
   value, full stop. Write-through eviction is exactly one
   `cold.Put(coldTable, key, storage.Get(offset))` of that current value (not
   a replay of "pending changes," of which there may have been several, since
   only the final value matters to mdbx), read straight from the table's own
   storage right before the row is dropped. This needs no pending-writes
   index, no ring scan, and no per-row LSN watermark — and keeps the ring
   buffer purely a catch-up cache with no eviction-correctness obligation,
   consistent with risk #5 as already written.

## Client sync — what the WAL/ring provides and what it doesn't (2026-09-14)

Scope boundary with [Networking](04-networking.md)'s Stage 8: the WAL and ring
are the **supply side** of client propagation. A reconnecting client asking
"everything since cursor L" is fully served — LSN-indexed, operation-granular
(never a torn multi-table op), RAM-fast catch-up for short disconnects,
WAL-fallback for long ones, both table kinds recorded. But client sync has
four components, and the other three live in Stage 8, on top of this — not in
it:

1. **The subscription/fan-out engine** — the ring is a firehose per database;
   a client subscribes to *views* (keysets, predicates). Matching every ring
   entry to every session's interest set, per-session merging, and
   backpressure per priority is Stage 8's real work and its biggest risk. The
   ring is its input, not its substitute.
2. **The initial snapshot** — a first-time subscriber needs current view state
   serialized *then* diffs from a consistent cursor (snapshot taken at the
   same serialization point as the cursor assignment, so nothing is skipped or
   double-sent). The WAL is history, not current state; the snapshot path
   (memory scan + cold fallback — the merged cold+memory rule in Phase 3 is
   exactly this) is new Stage 8 code on top of existing pieces.
3. **Lossy delivery** — the ring is reliable-ordered, correct for standings
   and wrong for ball position on tick N (dropping is *correct* there). The
   `Delivery` classification lives in 04-networking's frame model; live lossy
   push is fan-out's job.
4. **The client-side schema/codec** — a connecting client has no C# types. The
   client-access pipeline (already declared as a separate wire format in
   Architecture's three-serialization-pipelines decision) needs a schema
   descriptor **generated from the `[Table]`/command attributes** — the
   generator is already the schema authority — plus generated codecs per
   client language. Server→client rows are product types:
   MessagePack-plus-generated-schema suffices; a SATS/BSATN-style algebraic
   type system (SpacetimeDB built one because their client surface spans four
   languages and WASM module boundaries with sum-typed reducer arguments)
   stays in the backlog until a real client language exists and MessagePack
   demonstrably falls short — same evidence gate as UDP and ART. The one place
   algebraic pressure is real: client→server *commands* are naturally a tagged
   union; revisit if that surface grows.

Why the WAL decisions made here matter downstream: operation-granular LSNs are
exactly the resume primitive fan-out and the snapshot path need; full-value
entries make catch-up entries self-contained (no "fetch base row first"
round-trip — which matters especially for Instant-table catch-up, where no
cold store exists to fetch from). Nothing in Phase 1–4 is invalidated by
Stage 8's needs; the boundary is clean.

