# Schema migration and contract generations

Status: design agreed 2026-09-14. To build when the first breaking schema change
is actually needed - nothing in `src/` implements any of this yet. Recorded now
because it constrains one decision that is cheap today and expensive later
(section 11).

## 1. Why this is needed at all: nothing on disk is self-describing

The problem is not "the schema changed" - it is that **old bytes are
indistinguishable from new bytes**:

- **Row and key bytes are raw, positional layout.** Persistent rows are
  unmanaged `record struct`s serialized through MemoryPack's unmanaged fast
  path (`Milestone3Tests.cs`: no `[MemoryPackable]` needed), and nothing in the
  repo uses `GenerateType.VersionTolerant` or `[MemoryPackOrder]`. The byte
  layout *is* the struct layout: field order, field sizes, padding.
- **The WAL frame carries no schema information** - `[length][crc32][lsn][kind]`
  plus `MemoryPack(tableId, kind, key, row)`: a table id and opaque bytes
  (`WalRecordCodec`).
- **libmdbx knows nothing but keys and values** - by design, and the right
  division of labour: the store owns bytes, codegen owns meaning.
- The only versioning that exists today is `WalFileHeaderCodec.CurrentVersion`,
  and `TryDecode` already refuses a header whose version it does not know.

Consequence, stated plainly: **adding, removing, reordering or retyping a row
field is not a decode error - it is a silent misread.** Old bytes decode into
wrong values with no exception, because nothing in the bytes contradicts the
new interpretation. A migration design therefore cannot rely on *detecting*
that the data is old; it needs an out-of-band marker.

## 2. Two artifacts, not one

"Snapshot" was doing two jobs in the first framing. They have different
purposes, costs and consumers:

1. **Contract descriptor (the schema snapshot).** Small, generator-emitted,
   git-versioned. Describes everything that affects on-disk bytes: the
   `tableId`/`Accessor` set, mdbx sub-db names, primary-key codec layout, row
   codec layout, index definitions, and the **generation**. Its payoff is
   build-time: the generator can diff generation N-1's descriptor against N
   and refuse to build a breaking change that has no registered migration step -
   the same "every rule is a diagnostic, no silent assumptions" culture as
   `RHINO001`-`RHINO010`. It is also what would make an old-format reader
   *generatable* rather than hand-written, if that is ever wanted.
2. **Pre-migration backup (the data snapshot).** Its purpose is **rollback**,
   not migration input: after the drain, mdbx *is* the generation-G data, so
   there is nothing a data snapshot can contribute as input. Taken after the
   drain (when the WAL is known empty) a plain file copy is a consistent
   snapshot - no special machinery - and it is the "if the migration is wrong,
   restore these files and run the old binary" escape hatch.

## 3. Generations and the open-time protocol

- `G_db` - stored in the existing mdbx metadata sub-db
  (`__rhinodb_checkpoint__`), alongside the checkpoint LSN watermark.
- `G_wal` - stored in the WAL file header.
- `G_binary` - a compile-time constant emitted by the generator from the
  descriptor.

```
open:
  1. G_wal != G_db              - REFUSE. Never interpret bytes written under
                                  another contract; the message names both
                                  generations and the remedy.
  2. G_db == G_binary           - normal startup (replay tail, then run).
  3. G_db below G_binary, and a
     migration step exists      - a. drain the tail (generation-G reader) and
                                     then truncate the WAL   [drain barrier]
                                  b. write the pre-migration backup
                                     [rollback artifact]
                                  c. ONE mdbx write txn: transform every
                                     persistent table rows (old reader,
                                     transform, new writer), rebuild secondary
                                     indexes and auto-increment counters from
                                     scratch, drop orphaned sub-dbs, write
                                     G_binary                       [atomic]
                                  d. continue normal startup.
  4. G_db above G_binary        - REFUSE (downgrades unsupported - decided).
  5. no migration step exists   - REFUSE (the generator diagnostic of section 6,
                                  mirrored at runtime).
```

Two properties worth keeping:

- **The generation is written in the same transaction as the data**, so
  "migrated" and "generation bumped" can never disagree - the same
  crash-window-closing move as `05-wal-design.md`'s single-transaction
  checkpoint.
- **A migration is indistinguishable from an ordinary restart for clients**:
  catch-up cursors live in the RAM ring, which dies on restart anyway, so
  clients always re-subscribe with a full resync. Nothing extra to build in
  [Networking](04-networking.md).

## 4. One reader, two consumers

Absorbing a pending WAL tail and migrating table rows need **the same
capability**: reading generation-G `key`/`row` bytes. They are therefore not two
mechanisms - the migration artifact is *a reader for generation G, a transform,
and the current writer*, consumed by both. Note also that a clean shutdown does
**not** empty the WAL today (`ColdStore.Dispose` does not truncate; only a
checkpoint does), so the pending-tail case is the *normal* upgrade path, not the
exception.

## 5. Schema-change taxonomy and migration consequence

| Change | Consequence |
|---|---|
| Add / remove / **reorder** / retype a row field | Silent misread (positional bytes) - breaking, needs a migration step |
| Primary key type change | Key bytes change **and** every index must be rebuilt; effectively a table rewrite |
| `Accessor` rename | `tableId` (FNV-1a of the accessor) *and* the mdbx sub-db name both change, so it looks like "new empty table + orphan". Must be an explicit rename step |
| Persistent vs Instant kind change | Data appears/disappears; breaking, per-direction decision |
| Table removal | Orphan sub-db - kept for one generation as a rollback window, then dropped (proposed) |
| Index add/remove/kind change | **Rebuilt, never migrated** - derived data |

**Rule: derived data is rebuilt, never migrated.** Secondary indexes and
auto-increment counters are recomputed from the rows (counters as max+1), which
also means a migration never has to understand their old encodings.

## 6. The generator's role (future work)

Emit the descriptor; diff `N-1` against `N` at build time; emit `G_binary`; and
report a new diagnostic (an `RHINO012`-class rule, alongside `RHINO011` and its
tableId-collision check) when an incompatible descriptor diff has no registered
migration step. That is what turns "someone forgot to write the migration" from
a production data-loss incident into a build error.

## 7. Atomicity and the crash matrix

- crash during the drain - WAL intact, retry.
- crash during the backup - backup incomplete, retry.
- crash during the migration transaction - txn aborted, database still at
  generation G, retry.
- crash after commit - nothing pending; the next run sees `G_db == G_binary`.

No partially-migrated state is reachable, which is exactly why the migration is
one transaction rather than a resumable script.

## 8. Scale note (deliberately deferred)

A full-database rewrite in one transaction is O(dataset) at every breaking
upgrade - fine at league scale, potentially minutes at 50M-row scale. Because
the store is schema-agnostic a lazy alternative exists (a per-row generation
byte, migrate-on-touch, background sweeper), but it makes per-row codec dispatch
mandatory and leaves the database legitimately mixed-generation for a while - a
different, more complex design. Not built until a measured startup time demands
it. Same evidence gate as UDP, ART and the algebraic client codec.

## 9. How migrations must be tested

Frozen fixtures from the **previous released build** - bytes produced by the old
code, checked in (or produced by a retained old codec inside the test project).
The trap to name explicitly: generating "old" data with the *current* codegen
codec makes the test circular and it passes even when the migration is wrong.
Assertions: values preserved, indexes and counters rebuilt, generation bumped,
re-running is a no-op, unknown-or-newer generation refuses.

## 10. Open items (proposed defaults, to confirm when built)

- **Additive changes do not bump the generation.** Adding a table (a new sub-db,
  no existing layout touched) is safe without a migration; anything that changes
  an existing layout's bytes does bump it. - proposed.
- **Orphan sub-dbs** on table removal: kept for one generation, then dropped.
  - proposed.
- **Downgrades**: explicitly unsupported; refuse to open. - decided.

## 11. Pre-release gate (the one time-sensitive item)

If the WAL header ever needs a schema-generation field, add it **while
`formatVersion` can still be bumped** (currently `1`). After the first release,
`formatVersion 1` exists in the wild and "field absent means generation 1"
becomes a permanent legacy special case. Recorded here as "decide before the
first release", not as a today-task.

## 12. Relationship to the WAL design and today's code

- `05-wal-design.md` owns durability, the drain barrier and the checkpoint
  protocol this doc depends on: `RunCheckpoint` is where the drain and the WAL
  truncate happen, and `CheckpointEngine` is where a future generation marker
  will live, next to the LSN watermark.
- The `WalUnknownTable` refusal added 2026-09-14 ("a WAL entry names a table
  that is not in the schema - refuse, do not guess") is the narrow, *detectable*
  special case of "these bytes belong to another contract". It stays its own
  error, with a distinct future sibling for a generation mismatch
  (`SchemaGenerationMismatch`) rather than overloading one message.
- The same refusal must guard all three truncate/drain sites: the `RunCheckpoint`
  truncate, the migration drain, and - once the generation exists - every open.
- Client-side consequences are nil: see section 3's restart-equivalence note and
  [Networking](04-networking.md)'s Stage 8 boundary.