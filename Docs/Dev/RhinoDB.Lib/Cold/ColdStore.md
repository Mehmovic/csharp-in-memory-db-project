# `src/RhinoDB.Lib/Cold/ColdStore.cs` — dev notes

Excerpts of the trailing comments trimmed from this file during the
2026-09-13 comment cleanup.

## `SafeNoSync`/`CreateDbi`/`ReadOnlyTxn`/`DefaultUnixMode` constants

- `SafeNoSync = 0x10000` — `MDBX_SAFE_NOSYNC`
- `CreateDbi = 0x40000` — `MDBX_CREATE`
- `ReadOnlyTxn = 0x20000` — `MDBX_RDONLY` / `MDBX_TXN_RDONLY`
- `DefaultUnixMode = 0b110_100_100` — `0644`: `rw-r--r--`

## `Open` — `env.SetGeometry(-1, sizeNowBytes, sizeUpperBytes, -1, -1, -1)`

`-1` everywhere = "keep current or use default" (per `mdbx.h`). `sizeUpperBytes`
(2026-09-13, default `-1` preserving the prior always-default behavior for
every existing caller) is the max-map-size ceiling. Needed for
`RhinoDB.Run.Server.Benchmark`'s billion-record persistent-table tier:
libmdbx's default geometry doesn't reserve nearly enough address space for
that scale, and `sizeUpper` is a virtual-address-space reservation (mmap-based,
pages fault in lazily), not an upfront disk allocation — safe to set
generously large without pre-consuming that much real disk space.

`sizeNowBytes` (also 2026-09-13, same default-preserving `-1`) sets the
*current* size instead of leaving it to grow incrementally from libmdbx's
small default, so a caller that knows its expected data size up front (like
a benchmark's `GlobalSetup`) can pre-size past it and avoid paying for a
growth/remap event mid-measurement. **Not** what explained the small-tier-
slower-than-large-tier `Insert`/`Update` pattern first observed in
`RhinoDB.Run.Server.Benchmark` - see `Docs/03-roadmap.md`'s 2026-09-13
entry for the real root cause (a first-write-to-a-region cost, most likely
Windows Defender real-time scanning, unrelated to libmdbx geometry). Kept as
a real, independently-useful knob regardless.

## `commitCoalescingWindow` / `EndScope` / `ScheduleCoalescedSync` / `RunCoalescedBatch`

Added 2026-09-13 (`Docs/02-architecture.md` § "Confirmed commit batching").
`Open`'s `commitCoalescingWindow` parameter defaults to `TimeSpan.Zero`,
which takes the exact prior code path (`FireSyncImmediately` - chain the new
sync onto whatever's pending via `ContinueWith`, one physical
`mdbx_env_sync_ex` call per `Confirmed` commit unless one's already in
flight to piggyback on for free). A positive value instead makes `EndScope`
wait up to that long after a commit before actually firing the sync
(`ScheduleCoalescedSync`/`RunCoalescedBatch`), giving other `Confirmed`
calls that land in the meantime a chance to share the same call - the same
idea as Postgres's `commit_delay`.

Correctness-critical detail: `openBatch` (a `TaskCompletionSource<int>?`)
tracks whether the window is still *open* (safe to piggyback on) versus
*closed* (`RunCoalescedBatch` has moved on to actually calling `env.Sync`,
or is about to) - cleared, under `syncLock`, the instant the delay elapses
and *before* awaiting `previous`/calling `RunSync()`. A caller that lands
after that point cannot piggyback (an already-executing-or-about-to-execute
sync call's flushed state was decided independent of this later commit -
`mdbx_env_sync_ex` flushes "whatever's currently dirty," not a snapshot
frozen at scheduling time, so joining a sync that already started could
silently under-report durability for the new commit). Native sync calls
still stay strictly serialized regardless of window value (`RunCoalescedBatch`
awaits `previous` - the pre-existing `pendingSync` chain - before calling
`RunSync()`), so this never allows two `mdbx_env_sync_ex` calls to run
concurrently, mirroring the un-coalesced path's own invariant.

**Not usable on Windows at its originally-intended 0-2 ms scale**, found by
benchmarking (`RhinoDB.Run.Server.Benchmark`'s `ConfirmedCoalescingBenchmarks`,
full writeup there and in `Docs/03-roadmap.md`'s 2026-09-13 entry): plain
`Task.Delay` cannot reliably wait for less than Windows' default ~15.6 ms
system timer tick, so any window in that range adds a flat ~15 ms tax
regardless of the requested value - verified against a direct
`Task.Delay(1..5)` probe, and confirmed the *mechanism itself* is correct
(not just "coalescing doesn't happen") by testing at 20 ms, which landed at
~31 ms (≈2 ticks) rather than the ~1.6 ms 16 independent commits would cost
- proof the batch really shared one fsync. Default stays `TimeSpan.Zero`
until a higher-resolution wait (e.g. `timeBeginPeriod(1)` or a spin-wait for
sub-tick windows) is built - not attempted here, since the "does the
mechanism itself work" question this session was actually about is now
answered, independent of that follow-up.

## `Open` — `isFreshDirectory` / parent-directory fsync

Added 2026-09-13, prompted by a LinkedIn post about the same class of bug in
SpacetimeDB (fsync a newly-created file, but not the directory entry that
makes it findable - a crash can leave the file durable but practically
unreachable). Checked RhinoDB's actual exposure directly: grepped the entire
vendored libmdbx source for any directory-fd fsync - found none, and
libmdbx's own docs don't mention it either, so this was a real, unaddressed
gap, not something already covered underneath. Full risk analysis (why it's
architecturally much narrower here than in a segment-rotating WAL) is in
`Docs/02-architecture.md`'s "Cold storage" section.

`isFreshDirectory` is computed *before* `MdbxEnvironment.Create`/`env.Open` -
`!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any()`.
Simple and correct specifically because a `ColdStore`'s directory is always
dedicated to that one instance (never shared/multi-purpose), so "had zero
files before Open" is an accurate proxy for "libmdbx is about to create its
files here for the first time," without needing to know libmdbx's exact
internal filenames. Real, unrelated thing learned while testing this: `env
.Open()` against a directory that doesn't exist yet *succeeds* - libmdbx
creates it. A first draft of the test suite wrongly assumed this would fail;
caught by the test's own wrong assertion, not a production bug.

The sync itself only runs if `isFreshDirectory` was true *and* `env.Open()`
actually succeeded (order matters: no point syncing a directory whose file-
creation attempt just failed). `DirectorySync.TrySync`'s failure is a real
`Result.Error(DbError.ColdStorageDirectorySyncFailed())`, not silently
ignored - matches the "anticipated failures surface as a Result, never
downgraded to SystemFailure" discipline already used for `ColdTable.Put`/
`Delete`'s own fix. Deliberately does *not* retry the directory check on
every subsequent `Open()` call against an already-populated directory (a
normal reopen) - the durability property this closes only matters at the
one moment new files are actually being created, and syncing an already-
settled directory on every reopen would be pure, pointless overhead.
