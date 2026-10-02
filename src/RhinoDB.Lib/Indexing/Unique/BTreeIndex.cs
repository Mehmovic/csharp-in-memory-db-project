using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

using RhinoDB.Lib.Settings;

namespace RhinoDB.Lib.Indexing;

public class BTreeIndex<TKey> : OrderedIndex<TKey> where TKey : IComparable<TKey>, IEquatable<TKey> {
    private struct IndexChunk(TKey[] keys, int[] offsets) {
        public readonly TKey[] Keys = keys;
        public readonly int[] Offsets = offsets;
        public int Count = 0;

        public readonly TKey MinKey => Count > 0 ? Keys[0] : default!;
        public readonly TKey MaxKey => Count > 0 ? Keys[Count - 1] : default!;
        public readonly bool IsFull => Count == Keys.Length;
    }

    private readonly int chunkCapacity;
    private readonly IComparer<TKey> comparer;
    private readonly List<IndexChunk> chunks;

    public int Count { get; private set; }
    internal int ChunkCount => chunks.Count;

    public BTreeIndex(int chunkSize = 256, IComparer<TKey>? comparer = null) {
        chunkCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, chunkSize));
        this.comparer = comparer ?? Comparer<TKey>.Default;
        chunks = [RentChunk(chunkCapacity)];
        Count = 0;
    }
    
    private BTreeIndex(List<IndexChunk> built, int count, int capacity, IComparer<TKey> cmp) {
        chunks = built;
        Count = count;
        chunkCapacity = capacity;
        comparer = cmp;
    }

    public Result<int> GetOffset(TKey key) {
        var chunkIdx = FindChunkContainingKey(key);
        if (chunkIdx < 0) return Result.Error(DbError.IndexKeyNotFound());

        ref readonly var chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key, comparer);
        if (internalIdx < 0) return Result.Error(DbError.IndexKeyNotFound());

        return chunk.Offsets[internalIdx];
    }

    public void Insert(TKey key, int offset) {
        var chunkIdx = FindTargetChunkForInsertion(key);
        ref var chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];

        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key, comparer);
        if (internalIdx < 0) internalIdx = ~internalIdx;

        if (chunk.IsFull) {
            SplitAndInsert(chunkIdx, internalIdx, key, offset);
        } else {
            if (internalIdx < chunk.Count) {
                Array.Copy(chunk.Keys, internalIdx, chunk.Keys, internalIdx + 1, chunk.Count - internalIdx);
                Array.Copy(chunk.Offsets, internalIdx, chunk.Offsets, internalIdx + 1, chunk.Count - internalIdx);
            }

            chunk.Keys[internalIdx] = key;
            chunk.Offsets[internalIdx] = offset;
            chunk.Count++;
        }

        Count++;
    }

    public void Delete(TKey key) {
        var chunkIdx = FindChunkContainingKey(key);
        if (chunkIdx < 0) return;

        ref var chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key, comparer);
        if (internalIdx < 0) return;

        var moveCount = chunk.Count - internalIdx - 1;
        if (moveCount > 0) {
            Array.Copy(chunk.Keys, internalIdx + 1, chunk.Keys, internalIdx, moveCount);
            Array.Copy(chunk.Offsets, internalIdx + 1, chunk.Offsets, internalIdx, moveCount);
        }

        chunk.Count--;
        Count--;
        MergeWithNeighborIfUnderfull(chunkIdx);
    }

    private void MergeWithNeighborIfUnderfull(int chunkIdx) {
        if (chunks.Count <= 1) return;

        var chunkSpan = CollectionsMarshal.AsSpan(chunks);
        if (chunkSpan[chunkIdx].Count >= chunkCapacity >> 2) return;

        if (chunkIdx + 1 < chunks.Count) {
            ref var chunk = ref chunkSpan[chunkIdx];
            ref var next = ref chunkSpan[chunkIdx + 1];
            if (chunk.Count + next.Count <= chunkCapacity) {
                Array.Copy(next.Keys, 0, chunk.Keys, chunk.Count, next.Count);
                Array.Copy(next.Offsets, 0, chunk.Offsets, chunk.Count, next.Count);
                chunk.Count += next.Count;

                var retired = chunkSpan[chunkIdx + 1];
                chunks.RemoveAt(chunkIdx + 1);
                ReturnChunk(ref retired);
                return;
            }
        }

        if (chunkIdx > 0) {
            ref var prev = ref chunkSpan[chunkIdx - 1];
            ref var chunk = ref chunkSpan[chunkIdx];
            if (prev.Count + chunk.Count <= chunkCapacity) {
                Array.Copy(chunk.Keys, 0, prev.Keys, prev.Count, chunk.Count);
                Array.Copy(chunk.Offsets, 0, prev.Offsets, prev.Count, chunk.Count);
                prev.Count += chunk.Count;

                var retired = chunkSpan[chunkIdx];
                chunks.RemoveAt(chunkIdx);
                ReturnChunk(ref retired);
            }
        }
    }

    public void UpdateOffset(TKey key, int newOffset) {
        var chunkIdx = FindChunkContainingKey(key);
        if (chunkIdx < 0) return;

        ref var chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key, comparer);
        if (internalIdx < 0) return;

        chunk.Offsets[internalIdx] = newOffset;
    }

    public void UpdateKey(TKey oldKey, TKey newKey, int newOffset) {
        Delete(oldKey);
        Insert(newKey, newOffset);
    }

    protected override StackArrayPoolContainer<int> ScanOffsets(
        IndexBound<TKey> from,
        IndexBound<TKey> to,
        FilterDescriptor<TKey>? filterParam = null
    ) {
        if (chunks.Count == 0 || Count == 0) return StackArrayPoolContainer<int>.Empty();

        var unbounded = !from.IsBounded && !to.IsBounded;
        using var offsetBuilder = unbounded
            ? StackArrayPoolContainerBuilder<int>.Create(Count)
            : StackArrayPoolContainerBuilder<int>.Create(Constants.OffsetBuilderInitialCapacity);

        var chunkSpan = CollectionsMarshal.AsSpan(chunks);
        var startChunk = from.IsBounded ? FindFirstChunkWithMaxKeyAtLeast(from.Key) : 0;
        var hasFilter = filterParam is not null;

        for (var c = startChunk; c < chunks.Count; c++) {
            ref readonly var chunk = ref chunkSpan[c];

            if (to.IsBounded) {
                var minCmp = comparer.Compare(chunk.MinKey, to.Key);
                if (minCmp > 0 || (minCmp == 0 && !to.IsInclusive)) break;
            }

            var internalIdx = 0;
            if (from.IsBounded) {
                internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, from.Key, comparer);
                if (internalIdx < 0) internalIdx = ~internalIdx;
                else if (!from.IsInclusive) internalIdx++;
            }

            if (hasFilter) {
                var f = filterParam!.Value;
                if (to.IsBounded) {
                    for (var i = internalIdx; i < chunk.Count; i++) {
                        if (f.MustExclude(chunk.Keys[i])) continue;
                        var cmp = comparer.Compare(chunk.Keys[i], to.Key);
                        if (cmp > 0 || (cmp == 0 && !to.IsInclusive)) return offsetBuilder.Build().Unwrap();
                        offsetBuilder.Add(chunk.Offsets[i]);
                    }
                } else {
                    for (var i = internalIdx; i < chunk.Count; i++) {
                        if (!f.MustExclude(chunk.Keys[i])) offsetBuilder.Add(chunk.Offsets[i]);
                    }
                }
            } else if (to.IsBounded) {
                for (var i = internalIdx; i < chunk.Count; i++) {
                    var cmp = comparer.Compare(chunk.Keys[i], to.Key);
                    if (cmp > 0 || (cmp == 0 && !to.IsInclusive)) return offsetBuilder.Build().Unwrap();
                    offsetBuilder.Add(chunk.Offsets[i]);
                }
            } else {
                for (var i = internalIdx; i < chunk.Count; i++) offsetBuilder.Add(chunk.Offsets[i]);
            }
        }

        return offsetBuilder.Build().Unwrap();
    }

    private int FindFirstChunkWithMaxKeyAtLeast(TKey from) {
        var low = 0;
        var high = chunks.Count - 1;
        var result = chunks.Count;
        ReadOnlySpan<IndexChunk> chunkSpan = CollectionsMarshal.AsSpan(chunks);

        while (low <= high) {
            var mid = low + (high - low) / 2;
            if (comparer.Compare(chunkSpan[mid].MaxKey, from) >= 0) {
                result = mid;
                high = mid - 1;
            } else {
                low = mid + 1;
            }
        }

        return result;
    }

    private int FindTargetChunkForInsertion(TKey key) {
        if (Count == 0) return 0;

        var idx = FindFirstChunkWithMaxKeyAtLeast(key);
        return idx < chunks.Count ? idx : chunks.Count - 1;
    }

    private int FindChunkContainingKey(TKey key) {
        var low = 0;
        var high = chunks.Count - 1;
        ReadOnlySpan<IndexChunk> chunkSpan = CollectionsMarshal.AsSpan(chunks);

        while (low <= high) {
            var mid = low + (high - low) / 2;
            ref readonly var chunk = ref chunkSpan[mid];

            if (comparer.Compare(chunk.MinKey, key) > 0) {
                high = mid - 1;
                continue;
            }
            if (comparer.Compare(chunk.MaxKey, key) < 0) {
                low = mid + 1;
                continue;
            }
            return mid;
        }
        return -1;
    }

    private void SplitAndInsert(int chunkIdx, int internalIdx, TKey key, int offset) {
        ref var oldChunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var newChunk = RentChunk(chunkCapacity);

        var splitPoint = oldChunk.Count >> 1;
        var moveCount = oldChunk.Count - splitPoint;

        Array.Copy(oldChunk.Keys, splitPoint, newChunk.Keys, 0, moveCount);
        Array.Copy(oldChunk.Offsets, splitPoint, newChunk.Offsets, 0, moveCount);

        oldChunk.Count = splitPoint;
        newChunk.Count = moveCount;

        if (internalIdx < splitPoint) {
            Array.Copy(oldChunk.Keys, internalIdx, oldChunk.Keys, internalIdx + 1, oldChunk.Count - internalIdx);
            Array.Copy(oldChunk.Offsets, internalIdx, oldChunk.Offsets, internalIdx + 1, oldChunk.Count - internalIdx);
            oldChunk.Keys[internalIdx] = key;
            oldChunk.Offsets[internalIdx] = offset;
            oldChunk.Count++;
        } else {
            var newIdx = internalIdx - splitPoint;
            Array.Copy(newChunk.Keys, newIdx, newChunk.Keys, newIdx + 1, newChunk.Count - newIdx);
            Array.Copy(newChunk.Offsets, newIdx, newChunk.Offsets, newIdx + 1, newChunk.Count - newIdx);
            newChunk.Keys[newIdx] = key;
            newChunk.Offsets[newIdx] = offset;
            newChunk.Count++;
        }

        chunks.Insert(chunkIdx + 1, newChunk);
    }
    
    static private IndexChunk RentChunk(int capacity) => new IndexChunk(ArrayPool<TKey>.Shared.Rent(capacity), ArrayPool<int>.Shared.Rent(capacity));

    static private void ReturnChunk(ref IndexChunk chunk) {
        ArrayPool<TKey>.Shared.Return(chunk.Keys);
        ArrayPool<int>.Shared.Return(chunk.Offsets);
        chunk = default;
    }

    static public BTreeIndex<TKey> BulkLoad(
        ReadOnlySpan<TKey> keys,
        ReadOnlySpan<int> offsets,
        int chunkSize = 256,
        IComparer<TKey>? comparer = null
    ) {
        if (keys.Length != offsets.Length)
            throw new ArgumentException($"{keys.Length} keys but {offsets.Length} offsets.", nameof(offsets));

        var capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, chunkSize));
        var cmp = comparer ?? Comparer<TKey>.Default;

        var sortedKeys = ArrayPool<TKey>.Shared.Rent(keys.Length);
        var sortedOffsets = ArrayPool<int>.Shared.Rent(keys.Length);
        try {
            keys.CopyTo(sortedKeys);
            offsets.CopyTo(sortedOffsets);
            var permutation = ArrayPool<int>.Shared.Rent(keys.Length);
            for (var i = 0; i < keys.Length; i++) permutation[i] = i;
            var keysRef = sortedKeys;
            permutation.AsSpan(0, keys.Length).Sort((a, b) => {
                var byKey = cmp.Compare(keysRef[a], keysRef[b]);
                return byKey != 0 ? byKey : a.CompareTo(b);
            });

            var orderedKeys = ArrayPool<TKey>.Shared.Rent(keys.Length);
            var orderedOffsets = ArrayPool<int>.Shared.Rent(keys.Length);
            for (var i = 0; i < keys.Length; i++) {
                orderedKeys[i] = sortedKeys[permutation[i]];
                orderedOffsets[i] = sortedOffsets[permutation[i]];
            }
            
            ArrayPool<TKey>.Shared.Return(sortedKeys);
            ArrayPool<int>.Shared.Return(sortedOffsets);
            sortedKeys = orderedKeys;
            sortedOffsets = orderedOffsets;
            
            var unique = 0;
            for (var i = 0; i < keys.Length; i++) {
                if (unique > 0 && cmp.Compare(sortedKeys[unique - 1], sortedKeys[i]) == 0) {
                    sortedKeys[unique - 1] = sortedKeys[i];
                    sortedOffsets[unique - 1] = sortedOffsets[i];
                    continue;
                }
                sortedKeys[unique] = sortedKeys[i];
                sortedOffsets[unique] = sortedOffsets[i];
                unique++;
            }

            var built = new List<IndexChunk>(Math.Max(1, (unique + capacity - 1) / capacity));
            for (var start = 0; start < unique; start += capacity) {
                var chunk = RentChunk(capacity);
                var take = Math.Min(capacity, unique - start);
                Array.Copy(sortedKeys, start, chunk.Keys, 0, take);
                Array.Copy(sortedOffsets, start, chunk.Offsets, 0, take);
                chunk.Count = take;
                built.Add(chunk);
            }

            if (built.Count == 0) built.Add(RentChunk(capacity));

            return new BTreeIndex<TKey>(built, unique, capacity, cmp);
        } finally {
            ArrayPool<TKey>.Shared.Return(sortedKeys);
            ArrayPool<int>.Shared.Return(sortedOffsets);
        }
    }
}
