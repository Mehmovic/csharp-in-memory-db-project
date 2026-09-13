# Roadmap

RhinoDB is a multi-month project built bottom-up, in small verified slices — not
rushed, but not directionless either. Each stage has a concrete definition of done
(tests green for that layer) before the next one starts, and the dependency order
below is fixed: each stage genuinely needs the ones above it to exist first.

```
storage → indexes → transactions → libmdbx (cold storage) → IDC → networking
```

**Build order revised 2026-09-13, diverging from stage numbering.** The
diagram above is the *dependency* order the numbering was originally chosen
to match; the actual *build* order is now: Stages 1-5 (done) → **Stage 7 +
Stage 8 together** (HTTP/WebSocket hosting, with the full subscribe-and-diff
client model, not a stub) → a tiny proof-of-concept game → **Stage 6, scoped
down** (IDC for in-process and same-machine peers only — cross-machine IDC
and the eventual UDP/RUDP transport layer both explicitly deferred to the
Backlog). This is not a numbering error to fix: Stage 8 never actually
depended on Stage 6 — it only ever needed `Change<TKey,TRow>` (real since
Stage 5) and its own transport (Stage 7's HTTP/WebSocket host), never IDC's
actor-to-actor transport. The old text saying otherwise was an artifact of
when "change propagation" was one ambiguous stage covering both actor-to-actor
and server-to-client delivery, before the 2026-09-13 IDC/client-access split
(see [Architecture — Inter-Database Communication](02-architecture.md#inter-database-communication-idc)).
Rationale for building this way: prove the whole stack works end to end with
a minimal real game before investing further in the harder, still-fully-
unbuilt distributed-systems work (IDC, then cross-machine IDC, then a custom
UDP transport) — that work is valuable for *scaling* a shipped game, not a
prerequisite for a first one.

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
paragraph, and (at the time) `PersistentTable<TKey,TRow>` (which absorbed
`Table`'s old logic directly rather than being retired the same way, since
persistent-kind codegen didn't exist yet to inline into) were what was left
of Stage 3's original shape. `PersistentTable` was itself retired once that
barrier was removed — see Stage 5. See
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

## Stage 5 — Cold storage (libmdbx) ✅ done

MemoryPack serialization (positional, no version tags), per-table sub-databases,
`Load`/`Evict`/`Peek` distinct from `Insert`/`Delete`. Imperative, per-table,
batched schema migration — versioning tracked as generated code, not runtime tags.
Real: native libmdbx binding, `ColdStore`/`ColdTable`, the async-durability-sync
design, the table generator's `TableKind.Persistent` support (`.Storage.Load/
Evict/Peek` wired through, cross-kind atomicity with `Instant` tables in one
`Transaction.Apply()`, Milestone 3), the secondary-index read-your-own-writes
overlay on `Instant`-kind tables (Milestone 4, `By{Field}`-style accessors see
this operation's own staged-but-unapplied writes, gated on `Dirty` so the
no-staged-writes case costs nothing extra), secondary indexes on `Persistent`-
kind tables too (2026-09-12, the old `RHINO006` restriction lifted), and — also
2026-09-12 — `PersistentTable<TKey,TRow>` retired from the codebase entirely,
on your explicit direction ("now let us go toward removing the Persistent
table and put the logic into the generator totally"): `ColdStore` gained a
narrow public surface (`OpenTable`/`Put`/`Get`/`Delete`/`Peek`/`IsScopeActive`)
generated code drives directly, so a `Persistent`-kind `Ops` class's fields/
constructor/reads are now identical in shape to an `Instant`-kind one (same
`storage`/`primaryIndex` fields) — `isPersistent` only adds a `coldTable`/`cold`
pair and one extra cold write-through call per `Apply()` case. Same "hand-prove
then delete the scaffold" treatment `Table<TKey,TRow>` got. Porting
`PersistentTable`'s own deleted test suites surfaced a real, pre-existing gap
independent of the retirement: `Validate()` never checked that an `Update`'s
target key actually exists (either table kind) - fixed. Also 2026-09-12,
closing out this stage's last open item: **eager-load-on-startup**, via
`[Table].Evictable` (default `false`) and a generated `{Db}Loader` class -
see [Architecture — Restart & recovery](02-architecture.md#restart--recovery)
for the full design (real libmdbx cursor bindings added to `RhinoDB.Native`
for the underlying full-table scan, concurrent per-table loads since libmdbx
is multi-reader, an unchecked bulk-insert fast path that skips `Validate()`
since eager-loaded data already passed those checks once). Also added this
same day: `Iter()` on every generated table (sequential enumeration of every
row currently in memory, real-storage-only) and `[Table].ChunkSize` (default
4096, was hardcoded). See
[Architecture — Transactions](02-architecture.md#transactions).

2026-09-13: **Milestone 5 — `DbError.Custom` business-rule validation, wired
into the generated `Validate()`/`Result` path.** A row type declares one or
more `internal`-or-more-visible `static DbError? Method(RowType row)` methods
tagged `[Validate]`; the generator calls every one of them, for both Insert
and Update (not Delete — nothing to check against a row being removed), right
after the existing structural checks (duplicate key, primary-key immutability,
unique-index conflicts) in that same table's `Validate()` — a non-null return
sets `lastError` and fails validation exactly like a structural check would,
so a business-rule violation gets the same free cross-table atomicity
guarantee (a later table's failure leaves an earlier table's staged Insert
unapplied) the structural checks already had. New `RHINO009` diagnostic
catches the two ways this can go wrong before it becomes a `CS0122` inside
generated code: wrong signature, or an accessibility below `internal`
(generated code calls it from a sibling class in the same assembly, so
`private` can never work).

2026-09-13: **`RhinoDB.Run.Server.Benchmark`** — BenchmarkDotNet project,
`[MemoryDiagnoser]`, exercising real generated Instant- and Persistent-kind
tables (not a hand-rolled stand-in) through `Get`/`Insert`/`Update` and, for
Persistent, both `Optimistic` and `Confirmed` propagation. `RecordCount` scales
each benchmark via `[ParamsSource]` (`BenchmarkScale.RecordCounts()`, not a
plain `[Params(...)]`) — 100/10k/1M run by default; 100M and 1B are gated
behind the `RHINODB_BENCH_INCLUDE_BILLION=1` environment variable, since the
top tier's memory/time cost turned out to exceed what a normal dev machine can
run casually (found by actually trying it, not guessed in advance) — the
ladder itself stays real and available, not deleted, just opt-in. Seeding
reuses the same public `Run`/`Insert` API a real consumer would, in
100k-row-per-`Run`-call batches to keep setup wall-time tractable at the top
tier (`GlobalSetup` cost isn't part of the reported measurement regardless).
Needed one small engine addition: `ColdStore.Open` gained an optional
`sizeUpperBytes` parameter (default `-1`, preserving prior behavior for every
existing caller) — libmdbx's default geometry doesn't reserve enough address
space for a billion-row persistent table, and `sizeUpper` is a virtual-address
reservation (mmap-based, pages fault in lazily), not upfront disk usage, so
sizing it generously (128 GB in the benchmark) costs nothing until actually
used. Verified end-to-end with a fast `--job Dry` smoke run at small scale
(`InsertConfirmed` already shows the expected fsync-cost signal vs.
`InsertOptimistic` at that scale — ~4.5ms vs ~1.9ms). Not yet built: schema
migration tooling.

## Stage 6 — Inter-Database Communication (IDC) ⏳ designed, not built, built after Stage 7+8

Renamed 2026-09-13 from "Change propagation" — that name was ambiguous between
this stage (actor-to-actor, i.e. database/shard-to-database/shard) and Stage 8
(server-to-client). They share the same underlying change-emission mechanism
but are separate concerns with independent wire-format needs; only this stage
is "IDC" now. `PropagationMode` (`Optimistic`/`Confirmed`, a per-`Run`-call
durability choice) is unrelated and keeps its name.

**Scoped down 2026-09-13, and moved after Stage 7+8 in build order** (see the
build-order note at the top of this doc): first slice covers only
`InProcessIdcTransport` and a same-machine local-IPC transport (named pipe or
Unix domain socket) — `NetworkIdcTransport` (cross-machine) and a future
UDP/RUDP transport layer are both explicitly deferred to the Backlog, not
part of this stage's definition of done. `VersionedMemoryPack` is deferred
alongside cross-machine IDC for the same reason — version skew across
processes restarted together from one deploy is a much weaker requirement
than skew across a network boundary during a rolling upgrade.

The generic, non-boxing `Change<TKey,TRow>` shape and per-table typed `Ops`
buffers this stage was always meant to use — **not** the `object`/`Action`-typed
placeholder sketched and rejected in design review on 2026-09-01 — are no
longer something this stage needs to build. They already exist and are real,
built for the table generator's transaction/batch-apply system (see
[Architecture — Transactions](02-architecture.md#transactions)). Stage 6's job
shrinks to delivery/fan-out on top of what's already there, not inventing the
shape itself.

**Design settled 2026-09-13** (not yet built — recorded ahead of
implementation since the shape affects the declarative surface):

- **Three independent serialization pipelines, not one.** (1) Cold storage:
  plain MemoryPack, fixed, never configurable — unrelated to this stage. (2)
  IDC (this stage): `[Database].Idc` declares `WireFormat.MemoryPack` (default)
  or `VersionedMemoryPack` — actor-to-actor only, never seen outside the
  process/machine boundary the operator controls. (3) Client access (Stage 8):
  its own, separate wire-format declaration (`VersionedMemoryPack` or
  `MessagePack` for non-C# clients) — genuinely independent from IDC's choice,
  since a database's peers and its clients can have entirely different
  version/language constraints.
- **Fully generated, no hand-declared wire type.** A first design pass had the
  generator emit a `[MemoryPackable]`-attributed wire struct for a *third-party*
  generator (MemoryPack's own) to complete — rejected once we confirmed this
  isn't just fragile, it's a hard limitation of the incremental generator
  model: a generator's `ForAttributeWithMetadataName` pipeline only ever scans
  the *original* user-written syntax, never another generator's `AddSource`
  output, regardless of registration order. Fix: for plain `WireFormat
  .MemoryPack`, the table generator emits `Serialize`/`Deserialize` methods
  that call MemoryPack's own public low-level primitives directly
  (`MemoryPackWriter<TBufferWriter>`/`MemoryPackReader` — the same building
  blocks MemoryPack's generator itself would have called, field by field, in
  declaration order since default MemoryPack is positional) — no intermediate
  wire *type* needed at all, no dependency on MemoryPack's generator running
  in combination with ours. This is safe specifically *because* IDC is
  RhinoDB-to-RhinoDB only — we own both ends of the wire, so replicating a
  simple, stable, positional encoding ourselves carries little drift risk.
  `MessagePack` (Stage 8, client-facing, genuinely external non-C# consumers)
  is deliberately **not** given the same treatment — interop correctness with
  real external clients matters more there than the boilerplate savings, so
  that path keeps a hand-declared, attributed DTO processed by the real
  MessagePack-CSharp generator. `VersionedMemoryPack` is a later increment
  either way (more intricate tagged encoding to replicate correctly).
- **Location-transparent transport.** IDC must not know or care whether a peer
  database lives in the same process, a different process on the same
  machine, or a different machine — one `IIdcTransport` interface
  (`SendAsync`/`Receive` over an opaque envelope: kind, serialized key,
  serialized row, sequence number) with three implementations
  (in-process `Channel<T>`, local IPC — named pipe/Unix domain socket, and
  network — TCP/QUIC/gRPC), swappable without touching sender/receiver logic.
  Deferred, profile-gated optimization: the in-process transport *may* skip
  serialization and hand the receiver an already-constructed value directly,
  since same-process is the one case where paying for bytes at all is a
  choice, not a requirement — not built until proven to matter.
- **Declarative surface** (attributes only — see
  [Architecture — Inter-Database Communication](02-architecture.md#inter-database-communication-idc)
  for the full pseudocode):
  ```csharp
  [Database(Idc = WireFormat.MemoryPack)]
  public partial class GameDb : DbContext<GameDbTransaction> { }

  [Table(TableKind.Persistent, typeof(GameDb), IdcDelivery = DeliveryGuarantee.Reliable)]
  public readonly partial record struct Club([PrimaryKey] int Id, string Name, decimal Balance);
  ```
  Nothing else is hand-written — wire (de)serialization, the sender hook at
  commit time, and the receiver's apply-through-`Run` loop are all generated.

Per-table delivery-guarantee declaration (`Reliable` vs. lossy), per-connection
sender loop with opportunistic last-write-per-key merge.

## Stage 7 — Networking / hosting ⏳ not started, built together with Stage 8

Minimal direct HTTP/WebSocket host. Built together with Stage 8 (not before
it, and not after — the two ship as one push, per the 2026-09-13 build-order
decision at the top of this doc): the goal of this combined push is a fully
working HTTP + WebSocket layer with the *real* Stage 8 subscribe-and-diff
client model behind it, proven against a tiny real game, before any IDC work
starts. Not a stub or a raw request/response placeholder — the full model
described in Stage 8 below.

## Stage 8 — View/Table-only client access, subscribe-and-diff ⏳ designed, not built, built together with Stage 7

Decided 2026-09-07, queued here rather than folded into Stage 5. Clients never
fetch via an ad-hoc RPC query — the only client-facing read path is subscribing
to a `View`/`Table`: one full snapshot on first subscribe, then only `Change<TKey,TRow>`
diffs afterward. Same "single call point" discipline already
used for writes (`DbContext.Run`) and for cold reads (`Peek`), applied to the
client boundary — no code path that bypasses the diffing machinery, so the
bandwidth win (heavy cost paid once at subscribe time, not on every read) is
structural, not just a convention. A raw JSON/HTTP query API may come later, but
as an additive transport over the same View snapshot/diff mechanism, not a
redesign of it.

**Dependency corrected 2026-09-13**: this stage does *not* need Stage 6 (IDC)
— that was an artifact of when "change propagation" was one ambiguous stage.
It needs `Change<TKey,TRow>` (real since Stage 5's table generator work) and
Stage 7 (a transport to push over), which is why the two are now built
together, ahead of Stage 6 rather than after it. Client access has its own
wire-format pipeline (`VersionedMemoryPack`/`MessagePack` — see
[Architecture — Inter-Database Communication § Three serialization
pipelines](02-architecture.md#three-serialization-pipelines-not-one)),
independent from whatever IDC ends up using — a database's peers and its
clients can have entirely different version/language constraints. Also needs
a subscription-matching/fan-out engine (routing each committed change to the
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
  serialization and IDC/client-access sender loops' outgoing batch buffers; not
  applied to `OrderedIndex.Range()`'s allocation until `Table<TRow>`/transactions/
  locking exist and profiling actually shows it matters.
- **Cross-machine IDC (`NetworkIdcTransport`) and `VersionedMemoryPack`** —
  Stage 6 as first built (2026-09-13 scope decision) only covers in-process and
  same-machine peers. Extending `IIdcTransport` with a network implementation
  (TCP/QUIC/gRPC) and adding version-tolerant encoding for the rolling-upgrade
  case both wait until an actual multi-machine deployment need exists — see
  [Architecture — Inter-Database Communication](02-architecture.md#inter-database-communication-idc).
- **UDP/RUDP transport layer** — a custom UDP-based (unreliable or
  selectively-reliable) transport for latency-sensitive live-match-tick state,
  as an alternative to WebSocket for that traffic class. Deferred until Stage
  7/8's WebSocket-based client access is proven against a real game and an
  actual latency/bandwidth problem shows up that WebSocket can't serve well
  enough — not built speculatively ahead of that evidence.
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
