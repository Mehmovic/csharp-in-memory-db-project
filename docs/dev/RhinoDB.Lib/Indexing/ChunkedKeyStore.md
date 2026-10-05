# `src/RhinoDB.Lib/Indexing/ChunkedKeyStore.cs` — dev notes

The source itself stays comment-free; this is the "why" behind its shape.
Headings match the symbol the note is about.

## File header

The shared storage underneath both `BTreeIndex<TKey,TCmp>` and
`NonUniqueBTreeIndex<TKey,TCmp>` (2026-10-03). Both used to carry ~400 lines
of near-identical chunk code each; they are now thin wrappers that differ
only in which search they use (`LowerBound` by key for the unique index,
`LowerBoundPair` by `(key, offset)` for the non-unique one).

Shape: a two-level sorted structure - a directory of chunks, each chunk a
sorted pair of parallel arrays (`Keys`, `Offsets`) of a fixed power-of-two
`capacity`. It is effectively a height-2 B+tree.

It is a mutable `struct` held in a non-readonly field of the index class, so
it lives inline in the index object (no extra pointer hop per operation).
Never copy it: every mutating call must go through the field.

## `maxKeys` / `maxOffsets` (the chunk directory)

The single biggest read-path change. The old code binary-searched the
`List<IndexChunk>` by reading `chunk.MinKey`/`chunk.MaxKey`, i.e.
`chunk.Keys[0]` / `chunk.Keys[Count - 1]` - a pointer chase into a different
heap array on every probe (one or two cache misses per step, ~12 steps at 1M
keys). `maxKeys[c]` mirrors the last key of chunk `c` in one contiguous array,
so the directory search touches a single dense array, exactly like a B+tree
inner node. Searching by max key alone is sufficient: "first chunk whose max
is >= key" is the only chunk that can hold `key`.

`maxOffsets[c]` is the offset paired with that max key; only the non-unique
`(key, offset)` search needs it, but keeping it always costs one int write
and keeps one code path.

Invariant: for every non-empty chunk `c`, `(maxKeys[c], maxOffsets[c])` equals
its last entry. Every mutation that can change a chunk's last entry (insert at
the end, delete of the last entry, split, merge, `SetOffsetAt` on the last
entry) updates it. Empty chunks only exist as the single chunk of an empty
store, and every search short-circuits on `count == 0`, so an empty chunk's
directory slot is never read.

## `Cmp` / `default(TCmp)`

`TCmp` is a struct type argument (`where TCmp : struct, IComparer<TKey>`), so
`default(TCmp).Compare` is a direct, inlinable call per closed generic
instantiation - the same devirtualization trick as `IRowMutator<TRow>`. The
old `IComparer<TKey>` field made every comparison an interface call.

## `LowerBound` / `UpperBound` / `LowerBoundPair`

Hand-written instead of `Array.BinarySearch`: the BCL only takes its fast path
for `Comparer<T>.Default`, cannot use a struct comparer type argument, and
returns "some" matching index - which forced the old non-unique code to walk
linearly backwards/forwards through duplicate runs. Lower/upper bound give the
run edges directly. `Unsafe.Add` over `MemoryMarshal.GetArrayDataReference`
drops the per-probe bounds check; `length <= array.Length` always holds.

## `LowerBoundPair` (non-unique ordering)

The non-unique index orders entries by `(key, offset)`, not insertion order
(decided 2026-10-03). That turns Insert, Delete and the run edges of
GetOffsets into O(log n) binary searches; the old Insert walked to the end of
the duplicate run and Delete scanned the whole run comparing offsets
(1.5 µs inserts, ~6x slower delete-one-of-many than the hash index). It also
makes the order of duplicates deterministic and identical between `Insert` and
`BulkLoad`.

## `Scan`

Resolves the start and end positions with one lower/upper-bound search each,
sizes the result buffer exactly, then copies whole `Offsets` spans with
`AddRange` - no per-element compare against the `to` bound and no per-element
`Add` (which built a `Result` and checked disposal per offset).

A filter (`GetOffsetsExcept` / an Include filter) is resolved the same way
(2026-10-04): the store finds the run of entries whose key compares equal to
`FilterDescriptor.Key`, bulk-copies everything before and after that run (for
Exclude), and only tests the entries INSIDE the run element by element with
`MustInclude`. The per-element test stays inside the run because the filter is
an equality predicate (`IEquatable<T>`) while the run is defined by the
comparer - for ordinal strings and numbers they agree, but a culture comparer
can call two unequal strings equal. `Scan_Except` went from a per-element
filter over the whole index to two memcpys plus a one-element check.

## `TryFind` / `SearchExactIn` — early exit

Unique point lookups use a three-way binary search that returns as soon as a
probe compares equal (both in the directory - a hit on `maxKeys[c]` resolves
to the chunk's last entry without searching the chunk - and inside the chunk),
instead of a lower bound plus a final equality compare. For keys that land on
a lucky probe this saves most of the search; on average it saves one or two
compares. Measured trigger: the fixed-probe `Lookup_Int`/`Lookup_String`
benchmarks hit the OLD layout's very first directory probe (`Rows / 2` was its
exact middle chunk), which made the old code look faster on that benchmark
than on any other key - `Lookup_*_Random` was added so this is not mistaken
for a regression again.

## Prefix mode (`UsesPrefix`, `anchor`, `skip`, `Chunk.Prefixes`, `maxPrefixes`)

Only for `TCmp == OrdinalStringComparer` (a JIT-time constant, so every other
instantiation compiles the prefix code away entirely). Added 2026-10-04.

Problem: a string search step dereferences a string that lives somewhere else
on the heap - ~17 cache misses per random lookup at 100k keys - and real key
sets share long prefixes ("player-0000…"), so even a hot compare walks a dozen
equal characters before it decides anything.

Mechanism:

- `anchor` / `skip`: a prefix that EVERY key currently in the store shares
  (`skip` = its length; `-1` while empty). It only ever shrinks while the store
  is non-empty: `AdmitKey` computes how much of the anchor a new key shares
  and, if less, shortens the anchor and rebuilds every packed prefix
  (`RebuildPrefixes`, O(n)). Deletes never grow it back - a smaller anchor is
  still correct, just less selective. It resets when the store drains to empty
  and is recomputed exactly (longest common prefix of first and last key) by a
  bulk load. The anchor is always a fresh string copy, never a key instance,
  so it cannot keep a deleted key alive.
- `Prefixes[i]`: the 4 UTF-16 code units of key `i` right after the anchor,
  packed big-endian into a `ulong`, missing characters as 0. Comparing two
  packed prefixes as unsigned integers orders them exactly like ordinal string
  compare whenever they differ (a shorter string's 0 padding sorts first, code
  units are unsigned). Equal packed prefixes say nothing, so the search falls
  back to the full `string.CompareOrdinal` - the only time a search step
  dereferences a key string.
- `maxPrefixes` mirrors `maxKeys` for the directory search.
- `Classify`: a probe key that does not share the anchor is decided with ONE
  ordinal compare of its first `skip` characters - below every key or above
  every key - without searching at all. That includes `null` (below every
  non-null key) and probes that are a proper prefix of the anchor.

Cost: 8 bytes per key for string indexes, an O(n) rebuild each time the
anchor shrinks (at most once per character of the original anchor, and in
practice only while the first few keys arrive), and a few extra stores per
insert/delete. Guarded by `BTreeStringPrefixTests`, which is differential
against `string.CompareOrdinal` and specifically includes null/empty/'\0'/
high-surrogate keys and a late key that shrinks the anchor.

Not applied to composite keys containing a string (`TupleComparer<…>`):
their order is decided by the first element first, so a string-only prefix
does not fit; they keep the plain comparer path.

## Vector search (`UsesVectorSearch`, `CountBelow`)

For `DefaultComparer<int|long|uint|ulong>` only (again a JIT-time constant),
added 2026-10-04. Every lower/upper-bound search - directory and chunk -
binary-searches down to a window of `VectorWindow` (16) entries and then
counts the entries below the key with `Vector256.LessThan`/`LessThanOrEqual`
+ `PopCount` instead of continuing to branch. The last ~4 steps of a binary
search are the ones that mispredict and they all sit inside one or two cache
lines anyway, so replacing them with 2-4 vector compares is pure win.

Measured in an isolated micro-benchmark on 256-entry int chunks: 8.6 -> 7.0 ns
with the chunk hot in cache, 51 -> 39 ns cache-cold (4,096 random chunks). A
full SIMD linear scan of the chunk was rejected without building it: it
touches all 16 cache lines of a 256-int chunk instead of ~3.

Floating-point keys are deliberately excluded: `Comparer<double>` orders NaN
first, a vector `LessThan` treats NaN as unordered, so the two would disagree.
The lane type must match the key's signedness - `BTreeVectorSearchTests`
mixes negatives and top-bit-set unsigned values into a differential workload
because a signed/unsigned slip only shows up on exactly those values.

## `SplitAndInsert` — append split

When the target chunk is full, is the LAST chunk, and the new key goes past
its last entry (the auto-increment / ascending-key shape), the new key starts
a fresh chunk on its own instead of splitting the full chunk in half. A
half-split there would leave every chunk behind the insertion front 50% empty
forever; the append split keeps ascending loads packed at 100%. Any other
full-chunk insert still splits in half so random inserts keep headroom on
both sides.

## Reference clearing (`RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()`)

Bug fixed 2026-10-03: for `string` (or any reference-carrying) keys, deleted
keys stayed reachable. Deleting shifts the tail left and decrements `Count`,
leaving the old last reference in the now-unused slot; retired chunks were
returned to `ArrayPool` without `clearArray`. Every vacated key slot (delete
shift, split tail, directory slot, pool return) is now cleared when `TKey` can
hold references. The check is a JIT-time constant, so value-type keys pay
nothing. Guarded by `BTreeKeyRetentionTests`.

## `Build` — the boxed `IComparer<TKey>` in the bulk sort

`sortedKeys.AsSpan().Sort(order.AsSpan(), (IComparer<TKey>)default(TCmp))`
boxes the comparer on purpose. On .NET 11 RC1 the struct-generic overload
`Span<TKey>.Sort(Span<TValue>, TComparer)` returned UNSORTED keys when
`TComparer` is a struct (reproduced with a trivial `x.CompareTo(y)` struct
comparer; the keys-only struct overload and the boxed `IComparer` overload are
both correct). Bulk load is a cold startup path, so the interface call per
compare is irrelevant there. Re-check against the GA runtime before removing
the cast - `BTreeBulkLoadTests` fails immediately if it regresses.

## `CollapseToLastWrite`

The sort is not stable, so "last write wins" for duplicate keys in a unique
bulk load is resolved explicitly: each equal-key run keeps the entry with the
highest original index (`order[j]`), which is exactly what sequential
`Insert`s would have left behind.

## `FromSorted` — fill factor and directory slack

Chunks are filled to 31/32 of capacity (see `BTreeBulkLoadTests` for the
measured split storm a 100% fill caused), and the directory arrays get ~12%
slack so the first splits after a cold load do not immediately reallocate it.
