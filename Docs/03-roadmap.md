# Roadmap

RhinoDB is a multi-month project built bottom-up, in small verified slices — not
rushed, but not directionless either. Each stage has a concrete definition of done
(tests green for that layer) before the next one starts, and the dependency order
below is fixed: each stage genuinely needs the ones above it to exist first.

```
storage → indexes → transactions → libmdbx (cold storage) → propagation → networking
```

## Stage 1 — Storage engine ✅ done

`DenseArray<T>` — dense packed struct array, swap-remove delete, chunked growth
with hysteresis. The physical foundation everything else sits on.

## Stage 2 — Indexes ✅ done

Four index types, all key-to-offset direct (no indirection):

- `HashIndex<TKey,TRow>` / `OrderedIndex<TKey,TRow>` — unique.
- `NonUniqueHashIndex<TKey,TRow>` / `NonUniqueOrderedIndex<TKey,TRow>` — non-unique.

All four support `Register`/`Deregister` (secondary-index role) as well as
`Insert`/`Delete` (primary-index role, storage-owning). Composite keys confirmed
working via `ValueTuple` with no new code. Fully tested, including chunk-boundary
crossings and swap-remove repoint edge cases.

## Stage 3 — `Table<TRow>` coordinator 🚧 in progress

One concrete hand-written example (`PlayerTable`) before any generic reusable
engine — proving the shape before generalizing it or generating it. The table owns
`DenseArray` + drives every index (primary included) purely through
`Register`/`Deregister`, so a delete's swap-remove correctly repoints *every*
index, not just the one that initiated it.

`PlayerTableTests.cs` is written as a TDD spec (currently red by design) covering
insert/delete/update, duplicate-key rollback across every index, the swap-delete
cross-index repoint, non-unique bucket siblings surviving a swap, chunk-boundary
crossings, primary-key immutability, and update rollback on a mid-rekey uniqueness
conflict. **Next concrete step: implement `Insert`/`Delete`/`Update` on
`PlayerTable` against that spec.**

Once that shape is proven by hand, generalize to `Table<TPk,TRow>` and — later —
generate it from a declarative table definition via source generator (see
[Performance Principles §5](01-performance-principles.md#5-source-generators-are-how-we-get-all-of-the-above-and-a-nice-api)).

## Stage 4 — Transactions & execution model ⏳ not started

Revised 2026-09-05 to move toward an actor-per-database model: one `Context`
(one `SingleWriterLoop`, one `Channel`) is the actor, and the single call
point `Context.Run<T>(Func<Context, Result<T>> operation)` replaces separate
reducer/procedure/view types — safe because `operation` is synchronous, so
there's no way to block the writer thread on I/O (see [Architecture](02-architecture.md#execution-model-async-at-the-edges-single-writer-at-the-center)).
No table-level locking of any kind — this supersedes the per-table
SWMR/batched-lock-acquisition design from 2026-09-01, which solved a torn-read
problem that no longer exists once reads (internal and subscription-driven
alike) are unified onto the same single-writer stream as writes. Scaling is
horizontal: more `Context`s (more database instances), not more concurrency
inside one.

Built from the start using the generic, non-boxing `Change<TKey,TRow>` shape and
typed op buffers — **not** the `object`/`Action`-typed placeholder that was
sketched and rejected in design review on 2026-09-01. Even before the generator
exists to emit these types, the hand-written version should already be in the
target shape, not a shortcut to be revisited later.

## Stage 5 — Cold storage (libmdbx) ⏳ not started

MemoryPack serialization (positional, no version tags), per-table sub-databases,
`Load`/`Evict`/`Peek` distinct from `Insert`/`Delete`. Imperative, per-table,
batched schema migration — versioning tracked as generated code, not runtime tags.

## Stage 6 — Change propagation ⏳ designed, not built

Per-table delivery-guarantee declaration (`Reliable` vs. lossy), per-connection
sender loop with opportunistic last-write-per-key merge.

## Stage 7 — Networking / hosting ⏳ not started

Minimal direct HTTP/WebSocket host once the core engine and propagation are solid.
Not before — networking is explicitly deferred so early effort stays on the parts
of the design that are actually novel and risky.

## Stage 8+ — Soccer manager game

The actual application. Persistent tables for clubs/finances/contracts/standings/
transfer history, instant tables for live match tick state, transfer transactions,
match/league update propagation. See [Overview](00-overview.md#the-real-target-an-online-soccer-manager-game).

## Backlog (deferred, not scheduled)

Tracked deliberately as *not yet* rather than *never*, so they don't get lost and
don't get built before they're needed:

- **B+tree replacement for `OrderedIndex`** — only if a specific table's measured
  profile proves `SortedSet` insufficient.
- **Spatial index** (quad-tree/R-tree/geohash) — only if a real bounding-box/
  proximity query need shows up that composite-key `Range` can't serve.
- **`ArrayPool<T>` pooling** — identified good fits are libmdbx commit
  serialization and the propagation sender loop's outgoing batch buffer; not
  applied to `OrderedIndex.Range()`'s allocation until `Table<TRow>`/transactions/
  locking exist and profiling actually shows it matters.

## How this roadmap should be used

Before starting a new stage, check the design doc (this folder, plus the live
artifact) for decisions already made about it — don't re-derive from scratch, and
don't skip ahead of the dependency order even when a later stage looks more
interesting. When a stage turns up a real design gap (like the 2026-09-01 locking
and closures/boxing corrections), fix it in the design first, record it, then
build — not the other way around.
