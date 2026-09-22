# Debug session capture: WAL timestamps and the instant-table sidecar

Status: design agreed 2026-09-22. Nothing in `src/` implements any of this yet.
Recorded now because one item is time-sensitive before the first release
(section 4), and because the second half of this design is only buildable
*before* the session it is meant to debug - capture that was never enabled
cannot be reconstructed after the fact.

## 1. The problem this solves

The target hosting model (an actor-style application) creates databases at
runtime - one directory, one libmdbx env, one live WAL per actor - and sessions
span wall-clock windows ("debug everything that happened between 12:00 and
1:00 across all actors"). The replay machinery for this already exists for
persistent tables: `WalArchive.ReadHistory` merges archived segments plus the
live tail into one LSN-ordered stream, and the generated
`{Db}Loader.LoadFromGenesis(db, cold, upToLsn:, onEntryApplied:)` replays it
into a fresh in-memory database, bypassing libmdbx entirely. Partial replay
(`upToLsn`) and per-entry observation (`onEntryApplied`) are already real.

Two gaps make "debug a session" impossible today:

- **Gap 1 - there is no time in the WAL.** LSNs are dense and per-database but
  carry no wall-clock mapping. The frame is
  `[length][crc32][lsn][kind][payload]` (`WalRecordCodec`), the file header is
  `[version][databaseId][generation]` (`WalFileHeaderCodec`). Nothing anywhere
  records *when* a transaction happened, so replay is addressable only as
  "up to LSN N" - never "up to 12:37" - and there is no way to map a wall-clock
  window onto the LSN range that would answer it.
- **Gap 2 - instant tables have no history anywhere.** Since the shared
  `LsnSequence` refactor, every transaction that dirties any table draws an
  LSN, but an instant-only transaction never calls `cold.Stage(...)` - no WAL
  frame exists for it, by design. The only record of instant changes is the
  RAM-only `ChangeRingBuffer`, which dies on restart. Replaying an actor's WAL
  therefore rebuilds every persistent table exactly and every instant table
  *empty*.

Consequence, stated plainly: **a debugged session's instant state is
unrecoverable after the process ends - not difficult, structurally impossible -
unless capture was on while it happened.**

## 2. Two systems, one goal

They are deliberately separate systems with different blast radii:

1. **WAL timestamps** make the *existing* durable event stream
   time-addressable. A core, format-level change, gated on the pre-release
   window. Serves production replay and post-mortem as much as debugging.
2. **The sidecar** gives instant tables a history. A debug-only, opt-in,
   parallel sink hung off the ring buffer's existing capture point. No WAL
   format change, no new core assumption, no durability contract.

They compose: the sidecar alone can already replay everything (it captures
every table, not just instant ones); once WAL timestamps exist, both streams
are time-addressable and cross-actor session windows become a merge by
timestamp instead of an LSN-range guess.

## 3. What already exists that this stands on

Nothing here needs a new serializer, capture point, or replay engine - the
Stage 5.5 close-out built all three prerequisites:

- **Per-table byte serialization for every table, instant included**
  (`SerializeKey`/`SerializeRow`/`DeserializeRow`, emitted unconditionally by
  `TableGenerator`) - instant rows were never byte-serializable before this;
  the sidecar's payloads are exactly these bytes.
- **The shared LSN sequence** (`LsnSequence`, per-database, seeded from
  `ColdStore.RecoveredLsn`) - one continuous change numbering covering instant
  and persistent alike, which is what makes sidecar and WAL frames mergeable
  by LSN at all.
- **The ring buffer's capture tap** (`ChangeRingBuffer.Record(tableId, kind,
  lsn, key, row)`, called from each dirty table's generated
  `Apply(long lsn)` on the single writer thread) - the call already carries
  the complete WAL-frame payload as bytes. The sidecar adds no new capture
  point; it adds a second sink at the existing one.

## 4. Gap 1 - WAL timestamps (the pre-release gate)

**What:** a per-entry timestamp in the frame - one `long` per *transaction*,
not per change. A transaction is "at" one moment by definition; its changes
are simultaneous, so per-change timestamps would multiply frame bytes for zero
debugging benefit. The frame header grows from 17 bytes
(`4 + 4 + 8 + 1`) to 25, and `WalFileHeaderCodec` grows from 24 to 32.

**Why in-frame rather than debug-tool-only:** (a) time-addressable replay of
persistent history must not require debug capture to have been switched on;
(b) post-mortem ("what did this actor do between 12:00 and 1:00") is a
question about the durable log; (c) archived segments reuse the same header
and frame formats, so the archive inherits time-addressability for free - one
decision, not two.

**Timestamp source:** one shared wall-clock source per host process, read once
per commit at `EndScope`. A single per-process source makes cross-actor
ordering within a host skew-free by construction; per-database sources would
reintroduce exactly the skew problem session merging then has to fight.
Cross-host correlation is out of scope (single-host actor model) - see the
open item in section 10.

**Cost:** 8 bytes per transaction plus one clock read per commit - noise next
to the fsync. Nothing on the read path pays for it until someone actually
filters by time.

**The gate - same shape as the migration doc's section 12:** as of 2026-09-22,
`WalFileHeaderCodec.CurrentVersion` is still `1`, nothing has been released,
and the generation field already rode in at version 1 (pre-release, so no
version bump was ever needed for it). The timestamp field must land **before
the first release**. After that, version-1 frames exist in the wild and "field
absent means unknown" becomes a permanent legacy special case on every read
path - replay filters, archive walks, and the debug tooling all dispatch on
format version forever. The whole point of recording this now is that the
window is open exactly once and closes silently.

## 5. Gap 2 - the sidecar (debug capture for instant tables)

**Principle: a disk-backed twin of the ring buffer.** Same capture point, same
payload shape, written to a debug-only file, read by nothing in production.
Every existing assumption that instant tables never touch durable storage
stays intact - the migration doc's instant exemption, genesis replay's
"nothing to do for instant tables by construction", the `WalUnknownTable`
refusal boundary, archive consolidation and pruning: none of them ever see
these bytes, because none of them look at `.ringsink` files.

**Capture point.** `ChangeRingBuffer.Record` grows one optional sink:

```csharp
public void Record(uint tableId, ChangeKind kind, long lsn, byte[] key, byte[]? row) {
    slots[(int)(writePosition % slots.Length)] = new RingEntry(lsn, new WalChange(tableId, kind, key, row));
    writePosition++;
    if (count < slots.Length) count++;
    sink?.Write(tableId, kind, lsn, key, row);
}
```

That is the entire core change: one field, one null check on the commit path.
The ring keeps answering "recent changes since LSN X" for connected clients
(its `TryGetChangesSince` contract, including `RingBufferGap`, is untouched);
the sidecar persists the same entries to disk before the ring overwrites them.
Mentally: **ring = the short-term replay cache for the live connection;
sidecar = the same stream, complete, for offline debugging.**

**What gets captured.** Everything the ring captures - both table kinds,
honoring the same per-table `RingBuffer = false` opt-out. One policy, two
sinks; splitting the policies is rejected as a second thing to reason about
with no use case. Capturing persistent tables too (redundant with the WAL) is
deliberate: one complete stream makes the debug tool trivial and
self-sufficient, and the redundancy costs only ephemeral debug disk.

**File format.** WAL-shaped frames - `WalRecordCodec.Encode(lsn, kind,
changes)` is exactly the right frame - inside a deliberately *different*
container: its own magic, a `.ringsink` extension, and a v1-from-birth header
carrying capture-start time, `databaseId`, schema generation, and a marker
that this is a debug stream. Nothing may ever mistake it for a
`wal-archive/` segment, and no durable-storage tool ever reads it.

- **A timestamp per entry, from day one.** The sidecar has no installed base
  and no durability contract, so it does not wait for the WAL's format gate.
  Time-addressable instant-table replay ("what was the match state at 12:37")
  can ship via the sidecar before WAL timestamps land at all.
- **Rotation and retention.** Size-based rotation with the same
  sequential-numbering scheme as `WalArchive.NextSegmentFileName`; retention
  is delete-oldest; the whole tree is disposable.

**Durability contract: none.** Flush per commit at most; no fsync; a crash may
lose the tail, and a torn frame is dropped the same way `Scan` drops one from
a WAL. That is not a defect - it is the property that keeps the sidecar out of
the commit path's cost model. **Nothing in production may ever depend on the
sidecar.** If some future feature needs *durable* instant history, that is a
different, deliberate design conversation that reopens the WAL design - not
this file quietly becoming load-bearing.

**Enable/disable.** Opt-in at open time, via the hosting layer (an
actor-runtime option when spawning an actor whose session should be
replayable). Default off: the off state is the single null check above.
Bookkeeping of *what was captured* - which actors, when, up to what LSN -
lives in the hosting layer's session manifest, next to the
`sessions/{id}/actors/{id}/` directory convention, not in RhinoDB.

## 6. Debug replay: how the streams are used

- **Sidecar alone** gives the full actor history, instant tables included:
  scan frames, filter by `lsn <= cutoff` or `timestamp <= cutoff`, deserialize
  rows with the generated `DeserializeRow`, and apply them through the
  tables' normal insert/update/delete paths in a fresh in-memory `{Db}`. The
  core generator emits no `ReplayApply` for instant tables - the debug harness
  is a *consumer* of serializers that already exist, not a new generated
  pipeline.
- **Sidecar + WAL merged by LSN** yields one dense stream: the WAL's
  persistent entries fill in authoritatively, the sidecar's instant entries
  fill the WAL's expected instant-only gaps, and overlap dedupes via the same
  `lsn <= lastSeenLsn` skip `WalArchive.MergeInOrder` already performs. Useful
  when the durable stream should be the one trusted for persistent rows.
- **Cross-actor session replay:** per-database LSN sequences do not correlate;
  the per-entry timestamps (single per-host source, section 4) do. "Everything
  in session S between 12:00 and 1:00" becomes: enumerate the session's actor
  directories via the manifest, read each one's stream(s), merge by timestamp,
  window-filter.
- **Process boundary:** replay runs in a separate host process, in the
  existing `RhinoRunMode.Replay` shape, against a *copy* of the session
  directory - never inside the live runloop. `LoadFromGenesis` builds a fresh
  in-memory database (pointing it at live state would double-apply history),
  and `ReadHistory`'s live-tail read races with ongoing appends on a live
  actor. The debug host is disposable; production never pauses.

## 7. What this deliberately does not do

- **No WAL write-through for instant tables.** It would put debug policy
  inside the frozen WAL format, require `ReplayApply` to be emitted for
  instant ops in the core generator, and contradict the documented
  "instant tables never reach the WAL by construction" assumptions that the
  migration design now encodes in two places. The sidecar achieves the same
  debugging outcome with none of that. Rejected.
- **No production surface.** No reader for `.ringsink` files exists in
  `RhinoDB.Lib`; the ring's semantics are unchanged; capture is invisible
  unless enabled.
- **No cross-host clock story.** Single-host actor model; multi-host session
  correlation is out of scope until a multi-host actor runtime exists.

## 8. Build order (proposed, when queued)

Each step independently green-buildable/testable:

1. **Sidecar sink + format** - the `sink?.` branch, the `.ringsink` container
   with per-entry timestamps, rotation, and unit tests reading written files
   back. Smallest step; unblocks instant-table replay with no format gate.
2. **Debug replay CLI** - replay a captured directory (sidecar-only) with a
   cutoff by LSN, then by timestamp; dump changes to the console; assert final
   state matches the live database's for a scripted workload.
3. **WAL timestamps** - frame + header growth, `EndScope` clock read, replay
   filter by timestamp, archive walk inherits. The pre-release gate (section
   4); must precede the first release, not the first need.
4. **Session/manifest integration** - hosting-layer concern (which actors are
   captured, directory layout, retention). Documented here, owned there.

## 9. How to test it

- **Capture correctness:** scripted workload over mixed instant/persistent
  transactions; assert the sidecar contains exactly the ring's entries, LSNs
  match the shared sequence, and instant rows round-trip through
  `DeserializeRow`.
- **Replay equivalence:** after N transactions, replay the sidecar from
  genesis into a fresh `{Db}` and assert every table - instant included -
  equals the live instance's state at that cutoff.
- **Merge correctness:** a mixed workload where replaying WAL alone leaves
  instant tables empty, WAL + sidecar merged leaves them exact, and no entry
  is applied twice (LSN dedupe).
- **The 12:00-1:00 test:** two actors, interleaved commits, timestamps from
  one shared source; assert a window filter returns exactly the entries
  committed in the window, in timestamp order, across both actors.
- **Off-by-default:** capture disabled produces no files and does not perturb
  ring behavior (existing ring tests stay green unchanged).

## 10. Open items (proposed defaults, to confirm when built)

- **Timestamp encoding:** `DateTime.UtcNow` ticks vs Unix milliseconds vs a
  Stopwatch-mapped monotonic counter. Proposed: UTC ticks - 8 bytes, greppable
  by humans in a hex dump, and strict monotonicity is not required for window
  filtering.
- **Pre-release archives across the version bump:** if internal pre-release
  databases with archives must survive the timestamp addition, decide whether
  version-1 segments decode with an "unknown time" marker or are refused with
  a named remediation. Only matters if such archives must be preserved; the
  clean default is refusing, consistent with `TryDecode` refusing unknown
  versions today.
- **Capture-only tables:** a table with `RingBuffer = false` is currently
  uncaptured by the sidecar too (one policy, two sinks). If a capture-only
  table is ever wanted, the sidecar gets its own flag then - not before.
- **Sidecar retention default:** per-session directory deleted wholesale when
  the session is archived, vs size-capped delete-oldest. Proposed: wholesale,
  with the manifest recording that the session is no longer replayable.

## 11. Relationship to the other docs and the plan

- `05-wal-design.md` owns the durability and checkpoint protocols; this doc
  extends its frame/header formats but adds no durability obligation - the
  sidecar is explicitly outside that contract.
- `06-schema-migration.md` section 12 is this doc's template for the
  pre-release gate, and its generation field shares the same header the
  timestamp grows; the two land in one pre-release window, not two.
- The Stage 5.5 plan (ring buffer + row serialization + shared LSN sequence)
  built the prerequisites in section 3; the actor-runtime wrapper described in
  that conversation (directory convention, lazy open-on-touch, session
  manifest) is the hosting layer this doc's enable/manifest work belongs to.





