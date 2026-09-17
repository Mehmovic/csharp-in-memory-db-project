using System.Numerics;
using System.Runtime.InteropServices;

using RhinoDB.Lib.Settings;

namespace RhinoDB.Lib.Indexing;

public class BTreeIndex<TKey> : OrderedIndex<TKey> where TKey : IComparable<TKey>, IEquatable<TKey> {
    private struct IndexChunk(int capacity) {
        public readonly TKey[] Keys = new TKey[capacity];
        public readonly int[] Offsets = new int[capacity];
        public int Count = 0;

        public readonly TKey MinKey => Count > 0 ? Keys[0] : default!;
        public readonly TKey MaxKey => Count > 0 ? Keys[Count - 1] : default!;
        public readonly bool IsFull => Count == Keys.Length;
    }

    private readonly int chunkCapacity;
    private readonly List<IndexChunk> chunks;

    public int Count { get; private set; }

    public BTreeIndex(int chunkSize = 256) {
        chunkCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, chunkSize));
        chunks = [new IndexChunk(chunkCapacity)];
        Count = 0;
    }

    public Result<int> GetOffset(TKey key) {
        var chunkIdx = FindChunkContainingKey(key);
        if (chunkIdx < 0) return Result.Error(DbError.IndexKeyNotFound());

        ref readonly IndexChunk chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key);
        if (internalIdx < 0) return Result.Error(DbError.IndexKeyNotFound());

        return chunk.Offsets[internalIdx];
    }

    public void Insert(TKey key, int offset) {
        var chunkIdx = FindTargetChunkForInsertion(key);
        ref IndexChunk chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];

        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key);
        if (internalIdx < 0) {
            internalIdx = ~internalIdx;
        }

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

        ref IndexChunk chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key);
        if (internalIdx < 0) return;

        var moveCount = chunk.Count - internalIdx - 1;
        if (moveCount > 0) {
            Array.Copy(chunk.Keys, internalIdx + 1, chunk.Keys, internalIdx, moveCount);
            Array.Copy(chunk.Offsets, internalIdx + 1, chunk.Offsets, internalIdx, moveCount);
        }

        chunk.Count--;
        Count--;

        if (chunk.Count == 0 && chunks.Count > 1) {
            chunks.RemoveAt(chunkIdx);
        }
    }

    public void UpdateOffset(TKey key, int newOffset) {
        var chunkIdx = FindChunkContainingKey(key);
        if (chunkIdx < 0) return;

        ref IndexChunk chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key);
        if (internalIdx < 0) return;

        chunk.Offsets[internalIdx] = newOffset;
    }

    public void UpdateKey(TKey oldKey, TKey newKey, int newOffset) {
        Delete(oldKey);
        Insert(newKey, newOffset);
    }

    protected override OffsetList Scan(
        IndexBound<TKey> from,
        IndexBound<TKey> to,
        FilterDescriptor<TKey>? filterParam = null
    ) {
        if (chunks.Count == 0 || Count == 0) return OffsetList.Empty();

        using var offsetBuilder = OffsetListBuilder.Create(Constants.OffsetBuilderInitialCapacity);
        var chunkSpan = CollectionsMarshal.AsSpan(chunks);
        var startChunk = from.IsBounded ? FindFirstChunkWithMaxKeyAtLeast(from.Key) : 0;

        for (var c = startChunk; c < chunks.Count; c++) {
            ref readonly IndexChunk chunk = ref chunkSpan[c];

            if (to.IsBounded) {
                var minCmp = chunk.MinKey.CompareTo(to.Key);
                if (minCmp > 0 || (minCmp == 0 && !to.IsInclusive)) break;
            }

            var internalIdx = 0;
            if (from.IsBounded) {
                internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, from.Key);
                if (internalIdx < 0) internalIdx = ~internalIdx;
                else if (!from.IsInclusive) internalIdx++;
            }

            for (var i = internalIdx; i < chunk.Count; i++) {
                if (filterParam is { } filter && filter.MustExclude(chunk.Keys[i])) continue;

                if (to.IsBounded) {
                    var cmp = chunk.Keys[i].CompareTo(to.Key);
                    if (cmp > 0 || (cmp == 0 && !to.IsInclusive)) return offsetBuilder.Build().Unwrap();
                }

                offsetBuilder.Add(chunk.Offsets[i]);
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
            if (chunkSpan[mid].MaxKey.CompareTo(from) >= 0) {
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
            ref readonly IndexChunk chunk = ref chunkSpan[mid];

            if (chunk.MinKey.CompareTo(key) <= 0 && chunk.MaxKey.CompareTo(key) >= 0) {
                return mid;
            }
            if (chunk.MaxKey.CompareTo(key) < 0) {
                low = mid + 1;
            } else {
                high = mid - 1;
            }
        }
        return -1;
    }

    private void SplitAndInsert(int chunkIdx, int internalIdx, TKey key, int offset) {
        ref IndexChunk oldChunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var newChunk = new IndexChunk(chunkCapacity);

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
}
