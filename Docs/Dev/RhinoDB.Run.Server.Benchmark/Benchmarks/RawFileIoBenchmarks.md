# `src/RhinoDB.Run.Server.Benchmark/Benchmarks/RawFileIoBenchmarks.cs` — dev notes

Added 2026-09-14 to answer one specific, decisive question left open by the
fresh-page-write investigation (`Docs/03-roadmap.md`'s 2026-09-13 entry):
is the "a never-before-touched page costs ~10x more to write than an
already-settled one" cost a general disk/OS phenomenon on this machine, or
specific to libmdbx's memory-mapped I/O path? Deliberately the smallest
possible experiment that could answer this - plain `FileStream` writes with
an explicit `Flush(flushToDisk: true)` (real `fsync`/`FlushFileBuffers` per
call), **no memory mapping anywhere in this file** - rather than building
any part of a real WAL to find out.

## Design

- `PreallocatedSize` (1 GB) set via `FileStream.SetLength` before any
  writes - the same pre-sizing methodology already used for libmdbx's
  `sizeNowBytes` (`Docs/Dev/RhinoDB.Lib/Cold/ColdStore.md`), so "does the
  file need to grow" isn't conflated with "is this specific region
  untouched." `SetLength` alone doesn't force real disk-block allocation
  for the extended range on NTFS (sparse until written) - matching
  precisely the condition being tested.
- `PageStride = 4096`, not `RecordSize` (64 B) - `AppendFreshPageWriteFsync`
  advances a full OS page per call specifically so every single write lands
  on a genuinely new 4 KB page, never sharing one with a previous call. Using
  a smaller stride would let ~64 consecutive "fresh" writes land on the same
  physical page after the first one touched it, diluting the very effect
  this benchmark exists to isolate.
- Offset `0` is written once during `[GlobalSetup]`, before either
  `[Benchmark]` method runs, specifically so `RewriteSettledPageWriteFsync`
  is always measuring a genuinely already-settled page - mirrors
  `PersistentTableBenchmarks`' `lookupKey` (part of the initial seed batch,
  already written before the timed `Update` benchmark runs).

## Result (2026-09-14, `--job short`)

`AppendFreshPageWriteFsync` 380.9 μs vs. `RewriteSettledPageWriteFsync`
376.4 μs - statistically indistinguishable. **No fresh-page penalty exists
in plain sequential file I/O.** This is the decisive evidence that
libmdbx's mmap-based copy-on-write path is where the ~900 μs-1 ms fresh-page
cost actually comes from (most likely page-fault handling when the OS first
backs a newly-dirtied *mapped* page with a real physical page/disk block) -
not a property of writing to disk on this machine in general. See
`Docs/03-roadmap.md`'s Backlog entry ("WAL-based durability, replacing
libmdbx as the write-durability path") for what this implies and what's
still just decided-in-principle, not built.
