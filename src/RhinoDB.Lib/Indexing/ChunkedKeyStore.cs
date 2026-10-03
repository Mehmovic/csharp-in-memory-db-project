using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace RhinoDB.Lib.Indexing;

internal struct ChunkedKeyStore<TKey, TCmp>
    where TKey : IComparable<TKey>, IEquatable<TKey>
    where TCmp : struct, IComparer<TKey> {
    private struct Chunk(TKey[] keys, ulong[]? prefixes, int[] offsets) {
        public readonly TKey[] Keys = keys;
        public readonly ulong[]? Prefixes = prefixes;
        public readonly int[] Offsets = offsets;
        public int Count = 0;
    }

    private readonly struct Probe(TKey key, ulong prefix) {
        public readonly TKey Key = key;
        public readonly ulong Prefix = prefix;
    }

    private enum Side : byte { Below, Inside, Above }

    private const int FillNumerator = 31;
    private const int FillDenominator = 32;
    private const int DirectorySlack = 4;
    private const int PrefixChars = 4;
    private const int VectorWindow = 16;

    private readonly int capacity;
    private Chunk[] chunks;
    private TKey[] maxKeys;
    private ulong[] maxPrefixes;
    private int[] maxOffsets;
    private int chunkCount;
    private int count;
    private string? anchor;
    private int skip;

    public readonly int Count => count;
    public readonly int ChunkCount => chunkCount;
    public readonly int Capacity => capacity;

    static private bool UsesPrefix => typeof(TCmp) == typeof(OrdinalStringComparer);

    static private bool UsesVectorSearch
        => typeof(TCmp) == typeof(DefaultComparer<int>)
        || typeof(TCmp) == typeof(DefaultComparer<long>)
        || typeof(TCmp) == typeof(DefaultComparer<uint>)
        || typeof(TCmp) == typeof(DefaultComparer<ulong>);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static private int CountBelow(ref TKey first, int length, TKey key, bool orEqual) {
        if (typeof(TKey) == typeof(int)) return CountBelow(ref Unsafe.As<TKey, int>(ref first), length, Unsafe.As<TKey, int>(ref key), orEqual);
        if (typeof(TKey) == typeof(long)) return CountBelow(ref Unsafe.As<TKey, long>(ref first), length, Unsafe.As<TKey, long>(ref key), orEqual);
        if (typeof(TKey) == typeof(uint)) return CountBelow(ref Unsafe.As<TKey, uint>(ref first), length, Unsafe.As<TKey, uint>(ref key), orEqual);
        return CountBelow(ref Unsafe.As<TKey, ulong>(ref first), length, Unsafe.As<TKey, ulong>(ref key), orEqual);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static private int CountBelow<T>(ref T first, int length, T key, bool orEqual)
        where T : unmanaged, IComparisonOperators<T, T, bool> {
        var below = 0;
        var i = 0;
        if (Vector256.IsHardwareAccelerated) {
            var probe = Vector256.Create(key);
            for (; i + Vector256<T>.Count <= length; i += Vector256<T>.Count) {
                var lane = Vector256.LoadUnsafe(ref Unsafe.Add(ref first, i));
                var mask = orEqual ? Vector256.LessThanOrEqual(lane, probe) : Vector256.LessThan(lane, probe);
                below += BitOperations.PopCount(mask.ExtractMostSignificantBits());
            }
        }
        for (; i < length; i++) {
            var value = Unsafe.Add(ref first, i);
            if (orEqual ? value <= key : value < key) below++;
        }
        return below;
    }

    public ChunkedKeyStore(int chunkSize) {
        capacity = NormalizeCapacity(chunkSize);
        chunks = new Chunk[DirectorySlack];
        maxKeys = new TKey[DirectorySlack];
        maxPrefixes = UsesPrefix ? new ulong[DirectorySlack] : [];
        maxOffsets = new int[DirectorySlack];
        chunks[0] = RentChunk(capacity);
        chunkCount = 1;
        count = 0;
        anchor = null;
        skip = -1;
    }

    private ChunkedKeyStore(int capacity, Chunk[] chunks, int chunkCount, int count, string? anchor, int skip) {
        this.capacity = capacity;
        this.chunks = chunks;
        maxKeys = new TKey[chunks.Length];
        maxPrefixes = UsesPrefix ? new ulong[chunks.Length] : [];
        maxOffsets = new int[chunks.Length];
        this.chunkCount = chunkCount;
        this.count = count;
        this.anchor = anchor;
        this.skip = skip;
        for (var c = 0; c < chunkCount; c++) {
            if (chunks[c].Count > 0) SetMaxFromLast(c);
        }
    }

    static private int NormalizeCapacity(int chunkSize)
        => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, chunkSize));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static private int Cmp(TKey a, TKey b) => default(TCmp).Compare(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static private string? AsString(TKey key) => Unsafe.As<TKey, string?>(ref key);

    static private ulong PrefixOf(string? key, int skip) {
        if (key is null) return 0;

        var tail = key.AsSpan(Math.Min(skip, key.Length));
        ulong prefix = 0;
        for (var j = 0; j < PrefixChars; j++) {
            prefix = (prefix << 16) | (j < tail.Length ? tail[j] : 0u);
        }
        return prefix;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ulong PrefixOf(TKey key) => UsesPrefix ? PrefixOf(AsString(key), skip) : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static private ref ulong PrefixRef(ulong[]? prefixes)
        => ref UsesPrefix ? ref MemoryMarshal.GetArrayDataReference(prefixes!) : ref Unsafe.NullRef<ulong>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static private int CmpAt(ref TKey keys, ref ulong prefixes, int i, in Probe probe) {
        if (UsesPrefix) {
            var prefix = Unsafe.Add(ref prefixes, i);
            if (prefix != probe.Prefix) return prefix < probe.Prefix ? -1 : 1;
        }
        return Cmp(Unsafe.Add(ref keys, i), probe.Key);
    }

    private readonly Side Classify(TKey key, out Probe probe) {
        if (!UsesPrefix) {
            probe = new Probe(key, 0);
            return Side.Inside;
        }

        var text = AsString(key);
        if (skip > 0) {
            if (text is null) {
                probe = default;
                return Side.Below;
            }
            var c = string.CompareOrdinal(text, 0, anchor, 0, skip);
            if (c != 0) {
                probe = default;
                return c < 0 ? Side.Below : Side.Above;
            }
        }

        probe = new Probe(key, PrefixOf(text, Math.Max(skip, 0)));
        return Side.Inside;
    }

    static private int LowerBoundIn(TKey[] keys, ulong[]? prefixes, int length, in Probe probe) {
        ref var firstKey = ref MemoryMarshal.GetArrayDataReference(keys);
        ref var firstPrefix = ref PrefixRef(prefixes);
        var low = 0;
        var remaining = length;
        while (remaining > (UsesVectorSearch ? VectorWindow : 0)) {
            var half = remaining >> 1;
            if (CmpAt(ref firstKey, ref firstPrefix, low + half, probe) < 0) {
                low += half + 1;
                remaining -= half + 1;
            } else {
                remaining = half;
            }
        }
        if (UsesVectorSearch) low += CountBelow(ref Unsafe.Add(ref firstKey, low), remaining, probe.Key, orEqual: false);
        return low;
    }

    static private int UpperBoundIn(TKey[] keys, ulong[]? prefixes, int length, in Probe probe) {
        ref var firstKey = ref MemoryMarshal.GetArrayDataReference(keys);
        ref var firstPrefix = ref PrefixRef(prefixes);
        var low = 0;
        var remaining = length;
        while (remaining > (UsesVectorSearch ? VectorWindow : 0)) {
            var half = remaining >> 1;
            if (CmpAt(ref firstKey, ref firstPrefix, low + half, probe) <= 0) {
                low += half + 1;
                remaining -= half + 1;
            } else {
                remaining = half;
            }
        }
        if (UsesVectorSearch) low += CountBelow(ref Unsafe.Add(ref firstKey, low), remaining, probe.Key, orEqual: true);
        return low;
    }

    static private int LowerBoundPairIn(TKey[] keys, ulong[]? prefixes, int[] offsets, int length, in Probe probe, int offset) {
        ref var firstKey = ref MemoryMarshal.GetArrayDataReference(keys);
        ref var firstPrefix = ref PrefixRef(prefixes);
        ref var firstOffset = ref MemoryMarshal.GetArrayDataReference(offsets);
        var low = 0;
        var remaining = length;
        while (remaining > 0) {
            var half = remaining >> 1;
            var mid = low + half;
            var c = CmpAt(ref firstKey, ref firstPrefix, mid, probe);
            if (c == 0) c = Unsafe.Add(ref firstOffset, mid).CompareTo(offset);
            if (c < 0) {
                low = mid + 1;
                remaining -= half + 1;
            } else {
                remaining = half;
            }
        }
        return low;
    }

    static private int SearchExactIn(TKey[] keys, ulong[]? prefixes, int length, in Probe probe, out bool found) {
        if (UsesVectorSearch) {
            var position = LowerBoundIn(keys, prefixes, length, probe);
            found = position < length && Cmp(keys[position], probe.Key) == 0;
            return position;
        }

        ref var firstKey = ref MemoryMarshal.GetArrayDataReference(keys);
        ref var firstPrefix = ref PrefixRef(prefixes);
        var low = 0;
        var high = length;
        while (low < high) {
            var mid = (low + high) >>> 1;
            var c = CmpAt(ref firstKey, ref firstPrefix, mid, probe);
            if (c < 0) {
                low = mid + 1;
            } else if (c > 0) {
                high = mid;
            } else {
                found = true;
                return mid;
            }
        }
        found = false;
        return low;
    }

    private readonly (int Chunk, int Index) End => (chunkCount - 1, chunks[chunkCount - 1].Count);

    public readonly (int Chunk, int Index) LowerBound(TKey key) {
        if (count == 0) return (0, 0);

        var side = Classify(key, out var probe);
        if (side == Side.Below) return (0, 0);
        if (side == Side.Above) return End;

        var c = LowerBoundIn(maxKeys, maxPrefixes, chunkCount, probe);
        if (c == chunkCount) return End;

        ref readonly var chunk = ref chunks[c];
        return (c, LowerBoundIn(chunk.Keys, chunk.Prefixes, chunk.Count, probe));
    }

    public readonly (int Chunk, int Index) UpperBound(TKey key) {
        if (count == 0) return (0, 0);

        var side = Classify(key, out var probe);
        if (side == Side.Below) return (0, 0);
        if (side == Side.Above) return End;

        var c = UpperBoundIn(maxKeys, maxPrefixes, chunkCount, probe);
        if (c == chunkCount) return End;

        ref readonly var chunk = ref chunks[c];
        return (c, UpperBoundIn(chunk.Keys, chunk.Prefixes, chunk.Count, probe));
    }

    public readonly (int Chunk, int Index) LowerBoundPair(TKey key, int offset) {
        if (count == 0) return (0, 0);

        var side = Classify(key, out var probe);
        if (side == Side.Below) return (0, 0);
        if (side == Side.Above) return End;

        var c = LowerBoundPairIn(maxKeys, maxPrefixes, maxOffsets, chunkCount, probe, offset);
        if (c == chunkCount) return End;

        ref readonly var chunk = ref chunks[c];
        return (c, LowerBoundPairIn(chunk.Keys, chunk.Prefixes, chunk.Offsets, chunk.Count, probe, offset));
    }

    public readonly bool TryFind(TKey key, out int chunkIdx, out int index) {
        chunkIdx = 0;
        index = 0;
        if (count == 0) return false;
        if (Classify(key, out var probe) != Side.Inside) return false;

        var c = SearchExactIn(maxKeys, maxPrefixes, chunkCount, probe, out var onMax);
        if (c == chunkCount) return false;

        ref readonly var chunk = ref chunks[c];
        chunkIdx = c;
        if (onMax) {
            index = chunk.Count - 1;
            return true;
        }

        index = SearchExactIn(chunk.Keys, chunk.Prefixes, chunk.Count - 1, probe, out var found);
        return found;
    }

    public readonly bool TryFindPair(TKey key, int offset, out int chunkIdx, out int index) {
        (chunkIdx, index) = LowerBoundPair(key, offset);
        ref readonly var chunk = ref chunks[chunkIdx];
        return index < chunk.Count && chunk.Offsets[index] == offset && Cmp(chunk.Keys[index], key) == 0;
    }

    public readonly int OffsetAt(int chunkIdx, int index) => chunks[chunkIdx].Offsets[index];

    public readonly void SetOffsetAt(int chunkIdx, int index, int offset) {
        ref var chunk = ref chunks[chunkIdx];
        chunk.Offsets[index] = offset;
        if (index == chunk.Count - 1) maxOffsets[chunkIdx] = offset;
    }

    public void InsertAt((int Chunk, int Index) position, TKey key, int offset) {
        AdmitKey(key);
        var prefix = PrefixOf(key);
        var (c, i) = position;

        if (chunks[c].Count == capacity) {
            SplitAndInsert(c, i, key, prefix, offset);
        } else {
            ref var chunk = ref chunks[c];
            InsertInto(ref chunk, i, key, prefix, offset);
            if (i == chunk.Count - 1) SetMax(c, key, prefix, offset);
        }

        count++;
    }

    public void RemoveAt(int chunkIdx, int index) {
        ref var chunk = ref chunks[chunkIdx];

        var tail = chunk.Count - index - 1;
        if (tail > 0) {
            chunk.Keys.AsSpan(index + 1, tail).CopyTo(chunk.Keys.AsSpan(index));
            if (UsesPrefix) chunk.Prefixes.AsSpan(index + 1, tail).CopyTo(chunk.Prefixes.AsSpan(index));
            chunk.Offsets.AsSpan(index + 1, tail).CopyTo(chunk.Offsets.AsSpan(index));
        }

        chunk.Count--;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) chunk.Keys[chunk.Count] = default!;
        count--;

        if (chunk.Count == 0) {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) maxKeys[chunkIdx] = default!;
        } else if (index == chunk.Count) {
            SetMaxFromLast(chunkIdx);
        }

        MergeWithNeighborIfUnderfull(chunkIdx);

        if (count == 0) {
            anchor = null;
            skip = -1;
        }
    }

    public readonly StackArrayPoolContainer<int> Scan(
        IndexBound<TKey> from,
        IndexBound<TKey> to,
        FilterDescriptor<TKey>? filter
    ) {
        if (count == 0) return StackArrayPoolContainer<int>.Empty();

        var start = !from.IsBounded ? (0, 0)
            : from.IsInclusive ? LowerBound(from.Key) : UpperBound(from.Key);
        var end = !to.IsBounded ? End
            : to.IsInclusive ? UpperBound(to.Key) : LowerBound(to.Key);

        var total = CountBetween(start, end);
        if (total <= 0) return StackArrayPoolContainer<int>.Empty();

        var offsetBuilder = StackArrayPoolContainerBuilder<int>.Create(total);
        try {
            if (filter is not { } f) {
                CopyBetween(ref offsetBuilder, start, end);
            } else {
                var runStart = Max(start, LowerBound(f.Key));
                var runEnd = Min(end, UpperBound(f.Key));
                if (Before(runStart, runEnd)) {
                    if (!f.IsInclude) CopyBetween(ref offsetBuilder, start, runStart);
                    CopyFilteredBetween(ref offsetBuilder, runStart, runEnd, f);
                    if (!f.IsInclude) CopyBetween(ref offsetBuilder, runEnd, end);
                } else if (!f.IsInclude) {
                    CopyBetween(ref offsetBuilder, start, end);
                }
            }
            return offsetBuilder.Build().Unwrap();
        } finally {
            offsetBuilder.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static private bool Before((int Chunk, int Index) a, (int Chunk, int Index) b)
        => a.Chunk < b.Chunk || (a.Chunk == b.Chunk && a.Index < b.Index);

    static private (int Chunk, int Index) Max((int Chunk, int Index) a, (int Chunk, int Index) b) => Before(a, b) ? b : a;
    static private (int Chunk, int Index) Min((int Chunk, int Index) a, (int Chunk, int Index) b) => Before(a, b) ? a : b;

    private readonly int CountBetween((int Chunk, int Index) start, (int Chunk, int Index) end) {
        if (!Before(start, end)) return 0;
        var total = 0;
        for (var c = start.Chunk; c <= end.Chunk; c++) {
            var begin = c == start.Chunk ? start.Index : 0;
            var stop = c == end.Chunk ? end.Index : chunks[c].Count;
            total += stop - begin;
        }
        return total;
    }

    private readonly void CopyBetween(ref StackArrayPoolContainerBuilder<int> builder, (int Chunk, int Index) start, (int Chunk, int Index) end) {
        if (!Before(start, end)) return;
        for (var c = start.Chunk; c <= end.Chunk; c++) {
            ref readonly var chunk = ref chunks[c];
            var begin = c == start.Chunk ? start.Index : 0;
            var stop = c == end.Chunk ? end.Index : chunk.Count;
            if (stop > begin) builder.AddRange(chunk.Offsets.AsSpan(begin, stop - begin));
        }
    }

    private readonly void CopyFilteredBetween(
        ref StackArrayPoolContainerBuilder<int> builder,
        (int Chunk, int Index) start,
        (int Chunk, int Index) end,
        FilterDescriptor<TKey> filter
    ) {
        for (var c = start.Chunk; c <= end.Chunk; c++) {
            ref readonly var chunk = ref chunks[c];
            var begin = c == start.Chunk ? start.Index : 0;
            var stop = c == end.Chunk ? end.Index : chunk.Count;
            for (var i = begin; i < stop; i++) {
                if (filter.MustInclude(chunk.Keys[i])) builder.Add(chunk.Offsets[i]);
            }
        }
    }

    public void Release() {
        for (var c = 0; c < chunkCount; c++) ReturnChunk(chunks[c]);
        chunks = [];
        maxKeys = [];
        maxPrefixes = [];
        maxOffsets = [];
        chunkCount = 0;
        count = 0;
        anchor = null;
        skip = -1;
    }

    private void AdmitKey(TKey key) {
        if (!UsesPrefix) return;

        var text = AsString(key);
        if (skip < 0) {
            anchor = text is null ? string.Empty : new string(text.AsSpan());
            skip = anchor.Length;
            return;
        }

        var shared = text is null ? 0 : text.AsSpan(0, Math.Min(skip, text.Length)).CommonPrefixLength(anchor.AsSpan(0, skip));
        if (shared == skip) return;

        skip = shared;
        anchor = new string(anchor.AsSpan(0, shared));
        RebuildPrefixes();
    }

    private readonly void RebuildPrefixes() {
        for (var c = 0; c < chunkCount; c++) {
            ref readonly var chunk = ref chunks[c];
            for (var i = 0; i < chunk.Count; i++) chunk.Prefixes![i] = PrefixOf(AsString(chunk.Keys[i]), skip);
            if (chunk.Count > 0) maxPrefixes[c] = chunk.Prefixes![chunk.Count - 1];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static private void InsertInto(ref Chunk chunk, int index, TKey key, ulong prefix, int offset) {
        var tail = chunk.Count - index;
        if (tail > 0) {
            chunk.Keys.AsSpan(index, tail).CopyTo(chunk.Keys.AsSpan(index + 1));
            if (UsesPrefix) chunk.Prefixes.AsSpan(index, tail).CopyTo(chunk.Prefixes.AsSpan(index + 1));
            chunk.Offsets.AsSpan(index, tail).CopyTo(chunk.Offsets.AsSpan(index + 1));
        }

        chunk.Keys[index] = key;
        if (UsesPrefix) chunk.Prefixes![index] = prefix;
        chunk.Offsets[index] = offset;
        chunk.Count++;
    }

    private void SplitAndInsert(int chunkIdx, int index, TKey key, ulong prefix, int offset) {
        var fresh = RentChunk(capacity);

        if (chunkIdx == chunkCount - 1 && index == chunks[chunkIdx].Count) {
            InsertInto(ref fresh, 0, key, prefix, offset);
            InsertChunkAt(chunkIdx + 1, fresh);
            return;
        }

        ref var old = ref chunks[chunkIdx];
        var splitPoint = old.Count >> 1;
        var moveCount = old.Count - splitPoint;

        old.Keys.AsSpan(splitPoint, moveCount).CopyTo(fresh.Keys);
        if (UsesPrefix) old.Prefixes.AsSpan(splitPoint, moveCount).CopyTo(fresh.Prefixes);
        old.Offsets.AsSpan(splitPoint, moveCount).CopyTo(fresh.Offsets);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) old.Keys.AsSpan(splitPoint, moveCount).Clear();

        old.Count = splitPoint;
        fresh.Count = moveCount;

        if (index < splitPoint) {
            InsertInto(ref old, index, key, prefix, offset);
        } else {
            InsertInto(ref fresh, index - splitPoint, key, prefix, offset);
        }

        SetMaxFromLast(chunkIdx);
        InsertChunkAt(chunkIdx + 1, fresh);
    }

    private void MergeWithNeighborIfUnderfull(int chunkIdx) {
        if (chunkCount <= 1) return;
        if (chunks[chunkIdx].Count >= capacity >> 2) return;

        if (chunkIdx + 1 < chunkCount && chunks[chunkIdx].Count + chunks[chunkIdx + 1].Count <= capacity) {
            MergeRightIntoLeft(chunkIdx);
            return;
        }

        if (chunkIdx > 0 && chunks[chunkIdx - 1].Count + chunks[chunkIdx].Count <= capacity) {
            MergeRightIntoLeft(chunkIdx - 1);
        }
    }

    private void MergeRightIntoLeft(int left) {
        var right = left + 1;
        ref var dst = ref chunks[left];
        ref var src = ref chunks[right];

        if (src.Count > 0) {
            src.Keys.AsSpan(0, src.Count).CopyTo(dst.Keys.AsSpan(dst.Count));
            if (UsesPrefix) src.Prefixes.AsSpan(0, src.Count).CopyTo(dst.Prefixes.AsSpan(dst.Count));
            src.Offsets.AsSpan(0, src.Count).CopyTo(dst.Offsets.AsSpan(dst.Count));
            dst.Count += src.Count;
            maxKeys[left] = maxKeys[right];
            if (UsesPrefix) maxPrefixes[left] = maxPrefixes[right];
            maxOffsets[left] = maxOffsets[right];
        }

        ReturnChunk(src);
        RemoveChunkAt(right);
    }

    private void InsertChunkAt(int at, Chunk chunk) {
        if (chunkCount == chunks.Length) GrowDirectory();

        var tail = chunkCount - at;
        if (tail > 0) {
            chunks.AsSpan(at, tail).CopyTo(chunks.AsSpan(at + 1));
            maxKeys.AsSpan(at, tail).CopyTo(maxKeys.AsSpan(at + 1));
            if (UsesPrefix) maxPrefixes.AsSpan(at, tail).CopyTo(maxPrefixes.AsSpan(at + 1));
            maxOffsets.AsSpan(at, tail).CopyTo(maxOffsets.AsSpan(at + 1));
        }

        chunks[at] = chunk;
        chunkCount++;
        SetMaxFromLast(at);
    }

    private void RemoveChunkAt(int at) {
        var tail = chunkCount - at - 1;
        if (tail > 0) {
            chunks.AsSpan(at + 1, tail).CopyTo(chunks.AsSpan(at));
            maxKeys.AsSpan(at + 1, tail).CopyTo(maxKeys.AsSpan(at));
            if (UsesPrefix) maxPrefixes.AsSpan(at + 1, tail).CopyTo(maxPrefixes.AsSpan(at));
            maxOffsets.AsSpan(at + 1, tail).CopyTo(maxOffsets.AsSpan(at));
        }

        chunkCount--;
        chunks[chunkCount] = default;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) maxKeys[chunkCount] = default!;
    }

    private void GrowDirectory() {
        var length = chunks.Length * 2;
        Array.Resize(ref chunks, length);
        Array.Resize(ref maxKeys, length);
        if (UsesPrefix) Array.Resize(ref maxPrefixes, length);
        Array.Resize(ref maxOffsets, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void SetMax(int chunkIdx, TKey key, ulong prefix, int offset) {
        maxKeys[chunkIdx] = key;
        if (UsesPrefix) maxPrefixes[chunkIdx] = prefix;
        maxOffsets[chunkIdx] = offset;
    }

    private readonly void SetMaxFromLast(int chunkIdx) {
        ref readonly var chunk = ref chunks[chunkIdx];
        var last = chunk.Count - 1;
        SetMax(chunkIdx, chunk.Keys[last], UsesPrefix ? chunk.Prefixes![last] : 0, chunk.Offsets[last]);
    }

    static private Chunk RentChunk(int capacity)
        => new(
            ArrayPool<TKey>.Shared.Rent(capacity),
            UsesPrefix ? ArrayPool<ulong>.Shared.Rent(capacity) : null,
            ArrayPool<int>.Shared.Rent(capacity));

    static private void ReturnChunk(in Chunk chunk) {
        if (chunk.Keys is null) return;
        ArrayPool<TKey>.Shared.Return(chunk.Keys, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TKey>());
        if (chunk.Prefixes is not null) ArrayPool<ulong>.Shared.Return(chunk.Prefixes);
        ArrayPool<int>.Shared.Return(chunk.Offsets);
    }

    static public ChunkedKeyStore<TKey, TCmp> BuildUnique(ReadOnlySpan<TKey> keys, ReadOnlySpan<int> offsets, int chunkSize)
        => Build(keys, offsets, chunkSize, unique: true);

    static public ChunkedKeyStore<TKey, TCmp> BuildNonUnique(ReadOnlySpan<TKey> keys, ReadOnlySpan<int> offsets, int chunkSize)
        => Build(keys, offsets, chunkSize, unique: false);

    static private ChunkedKeyStore<TKey, TCmp> Build(ReadOnlySpan<TKey> keys, ReadOnlySpan<int> offsets, int chunkSize, bool unique) {
        if (keys.Length != offsets.Length)
            throw new ArgumentException($"{keys.Length} keys but {offsets.Length} offsets.", nameof(offsets));

        var rows = keys.Length;
        if (rows == 0) return new ChunkedKeyStore<TKey, TCmp>(chunkSize);

        var sortedKeys = ArrayPool<TKey>.Shared.Rent(rows);
        var sortedOffsets = ArrayPool<int>.Shared.Rent(rows);
        var order = ArrayPool<int>.Shared.Rent(rows);
        try {
            keys.CopyTo(sortedKeys);
            for (var i = 0; i < rows; i++) order[i] = i;
            sortedKeys.AsSpan(0, rows).Sort(order.AsSpan(0, rows), (IComparer<TKey>)default(TCmp));

            var length = unique
                ? CollapseToLastWrite(keys, offsets, sortedKeys, sortedOffsets, order, rows)
                : OrderDuplicateRunsByOffset(offsets, sortedKeys, sortedOffsets, order, rows);

            return FromSorted(sortedKeys.AsSpan(0, length), sortedOffsets.AsSpan(0, length), NormalizeCapacity(chunkSize));
        } finally {
            ArrayPool<TKey>.Shared.Return(sortedKeys, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TKey>());
            ArrayPool<int>.Shared.Return(sortedOffsets);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    static private int CollapseToLastWrite(
        ReadOnlySpan<TKey> keys,
        ReadOnlySpan<int> offsets,
        TKey[] sortedKeys,
        int[] sortedOffsets,
        int[] order,
        int rows
    ) {
        var written = 0;
        var i = 0;
        while (i < rows) {
            var latest = order[i];
            var j = i + 1;
            while (j < rows && Cmp(sortedKeys[j], sortedKeys[i]) == 0) {
                if (order[j] > latest) latest = order[j];
                j++;
            }

            sortedKeys[written] = keys[latest];
            sortedOffsets[written] = offsets[latest];
            written++;
            i = j;
        }
        return written;
    }

    static private int OrderDuplicateRunsByOffset(
        ReadOnlySpan<int> offsets,
        TKey[] sortedKeys,
        int[] sortedOffsets,
        int[] order,
        int rows
    ) {
        for (var i = 0; i < rows; i++) sortedOffsets[i] = offsets[order[i]];

        var start = 0;
        while (start < rows) {
            var end = start + 1;
            while (end < rows && Cmp(sortedKeys[end], sortedKeys[start]) == 0) end++;
            if (end - start > 1) sortedOffsets.AsSpan(start, end - start).Sort();
            start = end;
        }
        return rows;
    }

    static private ChunkedKeyStore<TKey, TCmp> FromSorted(ReadOnlySpan<TKey> keys, ReadOnlySpan<int> offsets, int capacity) {
        var rows = keys.Length;
        var fillCapacity = Math.Max(1, capacity * FillNumerator / FillDenominator);
        var chunkCount = Math.Max(1, (rows + fillCapacity - 1) / fillCapacity);

        string? anchor = null;
        var skip = -1;
        if (UsesPrefix) {
            var first = AsString(keys[0]);
            var last = AsString(keys[rows - 1]);
            skip = first is null || last is null ? 0 : first.AsSpan().CommonPrefixLength(last);
            anchor = skip == 0 ? string.Empty : new string(last!.AsSpan(0, skip));
        }

        var chunks = new Chunk[chunkCount + (chunkCount >> 3) + DirectorySlack];
        var perChunk = rows / chunkCount;
        var extra = rows % chunkCount;
        var start = 0;
        for (var c = 0; c < chunkCount; c++) {
            var chunk = RentChunk(capacity);
            var take = perChunk + (c < extra ? 1 : 0);
            keys.Slice(start, take).CopyTo(chunk.Keys);
            offsets.Slice(start, take).CopyTo(chunk.Offsets);
            if (UsesPrefix) {
                for (var i = 0; i < take; i++) chunk.Prefixes![i] = PrefixOf(AsString(chunk.Keys[i]), skip);
            }
            chunk.Count = take;
            chunks[c] = chunk;
            start += take;
        }

        return new ChunkedKeyStore<TKey, TCmp>(capacity, chunks, chunkCount, rows, anchor, skip);
    }
}
