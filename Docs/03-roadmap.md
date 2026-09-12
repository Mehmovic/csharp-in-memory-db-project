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

Five index types, all key-to-offset direct (no indirection):

- `HashIndex<TKey>` / `OrderedIndex<TKey>` — unique, implement `IUniqueIndex<TKey>`
  (`GetOffset`/`Insert`/`Delete`/`Range`).
- `NonUniqueHashSetIndex<TKey>` / `NonUniqueOrderedIndex<TKey>` — non-unique,
  implement `INonUniqueHash<TKey>` (`Insert`/`Delete`/`GetOffsets`).
  **`NonUniqueHashIndex<TKey>` (the original `Dictionary<TKey,List<int>>`-backed,
  O(n)-delete variant) removed 2026-09-11** — `NonUniqueHashSetIndex<TKey>`
  (`Dictionary<TKey,HashSet<int>>`, O(1) delete) is the sole non-unique-hash
  implementation now and the one `[Index(IndexKind.Hash)]` maps to; the
  `List`-backed variant was never reachable through the declarative surface
  and RhinoDB's own CSB+-tree (Backlog, below) is the intended eventual upgrade
  path anyway, not a second hand-maintained hash variant.

No `TRow` type parameter on the indexes themselves — they're pure key-to-offset
maps with no storage access of their own; `Table<TPk,TRow>` (Stage 3) is what
turns an offset back into a row. Composite keys confirmed working via
`ValueTuple` with no new code. Fully tested, including chunk-boundary crossings
and swap-remove repoint edge cases.

## Stage 3 — `Table<TPk,TRow>` coordinator ✅ done

Proven by hand first (`PlayerTable`, fully tested against `PlayerTableTests.cs`
including duplicate-key rollback across every index, swap-delete cross-index
repoint, non-unique bucket siblings surviving a swap, chunk-boundary crossings,
and primary-key immutability), then generalized to `Table<TPk,TRow>` — a
constructor-supplied `IUniqueIndex<TPk>` primary plus a `List<ISecondaryIndex<TRow>>`
registered at setup time, driven uniformly through `CheckInsert`/`Insert`/`Delete`
(see [Architecture](02-architecture.md#storage-engine-in-memory)). `TableTests.cs`
proved the generalized version reproduces `PlayerTableTests.cs`'s behavior
exactly, plus the self-collision fix `Update` needed once the table always
re-registers every secondary index rather than only the ones that actually
changed. **`PlayerTable`/`PlayerTableTests.cs` removed 2026-09-11** — their
proof was historical (this paragraph) and `TableTests.cs` already carries the
same coverage forward independently; keeping both was redundant once Part G's
generator turned out to prove itself against its own fixtures rather than
against `PlayerTable` (the original plan for that changed, see
[Architecture — Transactions](02-architecture.md#transactions)).
**`Table<TPk,TRow>` itself (and, in turn, `TableTests.cs`) removed
2026-09-12** — once the generator's own secondary-index retirement
(Milestone 2, see Architecture) proved codegen never needed `Table`'s
interface/delegate-driven engine, the same argument extended to its
primary-key slot (`IUniqueIndex<TPk>`, `Func<TRow,TKey> selector`): the
generator always knows a table's exact concrete primary-index type and
primary-key field name at generation time, so it now owns `DenseArray<TRow>`
+ a concrete index field directly, no coordinator type in between. This
paragraph, and `PersistentTable<TKey,TRow>` (which absorbed `Table`'s old
logic directly rather than being retired — still hand-written and
generically reusable pending Milestone 3), are what's left of Stage 3's
original shape. See
[Architecture § Storage engine](02-architecture.md#storage-engine-in-memory).

Next: generate this from a declarative table definition via source generator
(see [Performance Principles §5](01-performance-principles.md#5-source-generators-are-how-we-get-all-of-the-above-and-a-nice-api)) —
not scheduled yet.

## Stage 4 — Transactions & execution model ✅ done

Revised 2026-09-05 to move toward an actor-per-database model: one `DbContext`
(one `DbExecutionLoop`, one `Channel`) is the actor, and the single call
point `DbContext.Run<T>(Func<DbContext, Result<T>> operation)` replaces separate
reducer/procedure/view types — safe because `operation` is synchronous, so
there's no way to block the writer thread on I/O (see [Architecture](02-architecture.md#execution-model-async-at-the-edges-single-writer-at-the-center)).
No table-level locking of any kind — this supersedes the per-table
SWMR/batched-lock-acquisition design from 2026-09-01, which solved a torn-read
problem that no longer exists once reads (internal and subscription-driven
alike) are unified onto the same single-writer stream as writes. Scaling is
horizontal: more `DbContext`s (more database instances), not more concurrency
inside one.

Revised again 2026-09-06: `PropagationMode` defaults to `Optimistic`, not
`Confirmed` — a `Run` call returns as soon as the in-memory change is applied,
without waiting on durability confirmation, unless the caller opts into
`Confirmed` (or the `RunConfirmed` convenience overloads) for operations that
need to know the change survived a crash before proceeding. Still a no-op
distinction until Stage 5 (libmdbx) exists to actually differentiate the two.

Implemented as `DbContext` (single-writer actor) + `DbExecutionLoop` (its
private, non-reusable engine — takes the owning `DbContext` in its constructor,
`Enqueue` accepts `Func<DbContext, ...>` and `PropagationMode` directly) over a
`Channel<Func<PropagationMode>>` with `SingleReader = true, SingleWriter =
false`, matching the actual multi-caller/one-writer shape. An operation that
throws doesn't fault the returned `Task` — it comes back as an ordinary
`Result.Error` with `Kind == ErrorKind.SystemFailure`, carrying the real
exception (see [Architecture](02-architecture.md#error-handling)), so a caller
never needs both an `IsError()` check and a `try`/`catch` at the same call site.
Fully tested in `Execution/DbContextTests.cs` — round-trips, both `TArgs`
overloads, `RunConfirmed`, ordering under single- and multi-thread concurrent
submission, exception safety, and the no-adjacency-guarantee-across-separate-calls
property.

## Stage 5 — Cold storage (libmdbx) ✅ done (through Milestone 3)

MemoryPack serialization (positional, no version tags), per-table sub-databases,
`Load`/`Evict`/`Peek` distinct from `Insert`/`Delete`. Imperative, per-table,
batched schema migration — versioning tracked as generated code, not runtime tags.
Real: native libmdbx binding, `ColdStore`/`ColdTable`/`PersistentTable<TKey,TRow>`,
the async-durability-sync design, and — as of Milestone 3, 2026-09-12 — the table
generator's `TableKind.Persistent` support (a generated `Ops` class drives a real
`PersistentTable` directly, `.Storage.Load/Evict/Peek` wired through, cross-kind
atomicity with `Instant` tables in one `Transaction.Apply()`) — see
[Architecture — Transactions](02-architecture.md#transactions). Not yet built:
secondary indexes on `Persistent`-kind tables (`RHINO006`), schema migration
tooling, eager-load-on-startup.

## Stage 6 — Change propagation ⏳ designed, not built

The generic, non-boxing `Change<TKey,TRow>` shape and per-table typed `Ops`
buffers this stage was always meant to use — **not** the `object`/`Action`-typed
placeholder sketched and rejected in design review on 2026-09-01 — are no
longer something this stage needs to build. They already exist and are real,
built for the table generator's transaction/batch-apply system (see
[Architecture — Transactions](02-architecture.md#transactions)). Stage 6's job
shrinks to delivery/fan-out on top of what's already there, not inventing the
shape itself.

Per-table delivery-guarantee declaration (`Reliable` vs. lossy), per-connection
sender loop with opportunistic last-write-per-key merge.

## Stage 7 — Networking / hosting ⏳ not started

Minimal direct HTTP/WebSocket host once the core engine and propagation are solid.
Not before — networking is explicitly deferred so early effort stays on the parts
of the design that are actually novel and risky.

## Stage 8 — View/Table-only client access, subscribe-and-diff ⏳ designed, not built

Decided 2026-09-07, queued here rather than folded into Stage 5. Clients never
fetch via an ad-hoc RPC query — the only client-facing read path is subscribing
to a `View`/`Table`: one full snapshot on first subscribe, then only `Change<TKey,TRow>`
diffs (Stage 6's shape) afterward. Same "single call point" discipline already
used for writes (`DbContext.Run`) and for cold reads (`Peek`), applied to the
client boundary — no code path that bypasses the diffing machinery, so the
bandwidth win (heavy cost paid once at subscribe time, not on every read) is
structural, not just a convention. A raw JSON/HTTP query API may come later, but
as an additive transport over the same View snapshot/diff mechanism, not a
redesign of it.

Needs Stage 6 (the diff shape itself) and Stage 7 (a transport to push over) to
exist first — mechanically this stage is "who's allowed to read, and how" layered
on top of both, not new storage or propagation machinery of its own. Also needs a
subscription-matching/fan-out engine (routing each committed change to the
subscriptions that care about it) and a reconnect/catch-up story for a client
that missed diffs while disconnected — real scope, not a thin wrapper.

## Stage 9+ — Soccer manager game

The actual application. Persistent tables for clubs/finances/contracts/standings/
transfer history, instant tables for live match tick state, transfer transactions,
match/league update propagation. See [Overview](00-overview.md#the-real-target-an-online-soccer-manager-game).

## Backlog (deferred, not scheduled)

Tracked deliberately as *not yet* rather than *never*, so they don't get lost and
don't get built before they're needed:

- **B+tree replacement for `OrderedIndex`** — only if a specific table's measured
  profile proves `SortedSet` insufficient. `SortedSet`/`SortedDictionary` are
  red-black trees — correct, O(log n), but one key per heap-scattered node, so a
  range scan is real pointer-chasing. The concrete upgrade target, if this is ever
  triggered: not a plain B+tree (values-in-leaves-only, the textbook baseline —
  what libmdbx itself uses, right for *disk* pages) but a cache-sensitive variant
  suited to this being an in-memory, single-writer, read-optimized-range-scan
  structure — a CSB+-tree (children stored contiguously instead of one pointer
  each, more keys per cache line) with FAST's technique (SIMD compare against
  several keys at once per node, via `System.Runtime.Intrinsics`, node width sized
  to the target register's element count) grafted onto CSB+-tree's ordinary
  incrementally-mutable node layout rather than FAST's own bulk-load assumption.
- **Generation counter on `DenseArray` slots** — only if row references are ever
  cached across calls instead of always being re-looked-up by key. Would catch a
  stale handle pointing at a since-reused slot (after a swap-remove) rather than
  silently reading the wrong row. Not needed today since nothing caches a
  reference past a single lookup.
- **Spatial index** (quad-tree/R-tree/geohash) — only if a real bounding-box/
  proximity query need shows up that composite-key `Range` can't serve.
- **`ArrayPool<T>` pooling** — identified good fits are libmdbx commit
  serialization and the propagation sender loop's outgoing batch buffer; not
  applied to `OrderedIndex.Range()`'s allocation until `Table<TRow>`/transactions/
  locking exist and profiling actually shows it matters.
- **`[Database]` gains an `Accessor`-style name** — for a future networked/RPC
  access layer (`ctx.Db.Table.Insert(...)`, `ctx` being an RPC-scoped context,
  not `DbContext` itself) a remote client can't address a database by its C#
  type the way in-process code does, so it'll need *some* serializable name —
  the same reason `[Table]`'s `Accessor` exists for tables. Not added to
  `[Database]` yet: no networking layer exists to design the shape against
  (per Hosting/Deployment above — "no networking until the core engine is
  solid"), and database-addressing over RPC might not even end up being a
  flat string name (could be connection-scoped instead) — better to let that
  layer's actual design settle the question than guess now.

## How this roadmap should be used

Before starting a new stage, check the design doc (this folder, plus the live
artifact) for decisions already made about it — don't re-derive from scratch, and
don't skip ahead of the dependency order even when a later stage looks more
interesting. When a stage turns up a real design gap (like the 2026-09-01 locking
and closures/boxing corrections), fix it in the design first, record it, then
build — not the other way around.
