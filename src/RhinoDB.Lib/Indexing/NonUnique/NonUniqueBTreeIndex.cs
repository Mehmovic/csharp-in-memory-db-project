using System.Numerics;
using System.Runtime.InteropServices;

using RhinoDB.Lib.Settings;

namespace RhinoDB.Lib.Indexing;

public class NonUniqueBTreeIndex<TKey> : OrderedIndex<TKey> where TKey : IComparable<TKey>, IEquatable<TKey> {
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

    public NonUniqueBTreeIndex(int chunkSize = 256) {
        chunkCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, chunkSize));
        chunks = [new IndexChunk(chunkCapacity)];
        Count = 0;
    }

    public OffsetList GetOffsets(TKey key) {
        if (chunks.Count == 0 || Count == 0) return OffsetList.Empty();

        using var offsetBuilder = OffsetListBuilder.Create(Constants.OffsetBuilderInitialCapacity);
        var chunkSpan = CollectionsMarshal.AsSpan(chunks);
        for (var c = FindFirstChunkWithMaxKeyAtLeast(key); c < chunks.Count; c++) {
            ref readonly var chunk = ref chunkSpan[c];
            if (chunk.MinKey.CompareTo(key) > 0) return offsetBuilder.Build().Unwrap();

            var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key);
            if (internalIdx < 0) continue;
            while (internalIdx > 0 && chunk.Keys[internalIdx - 1].CompareTo(key) == 0) {
                internalIdx--;
            }

            for (var i = internalIdx; i < chunk.Count && chunk.Keys[i].CompareTo(key) == 0; i++) {
                offsetBuilder.Add(chunk.Offsets[i]);
            }
        }

        return offsetBuilder.Build().Unwrap();
    }

    public void Insert(TKey key, int offset) {
        var chunkIdx = FindTargetChunkForInsertion(key);
        ref var chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];

        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key);
        if (internalIdx < 0) {
            internalIdx = ~internalIdx;
        } else {
            // If duplicate exists, move past existing duplicates to insert stably
            while (internalIdx < chunk.Count && chunk.Keys[internalIdx].CompareTo(key) == 0) {
                internalIdx++;
            }
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

    public void Delete(TKey key, int offset) {
        if (chunks.Count == 0 || Count == 0) return;

        var chunkSpan = CollectionsMarshal.AsSpan(chunks);
        for (var c = FindFirstChunkWithMaxKeyAtLeast(key); c < chunks.Count; c++) {
            ref var chunk = ref chunkSpan[c];
            if (chunk.MinKey.CompareTo(key) > 0) return;

            var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key);
            if (internalIdx < 0) continue;

            var start = internalIdx;
            while (start > 0 && chunk.Keys[start - 1].CompareTo(key) == 0) {
                start--;
            }

            var end = internalIdx;
            while (end + 1 < chunk.Count && chunk.Keys[end + 1].CompareTo(key) == 0) {
                end++;
            }

            var targetIdx = -1;
            for (var i = start; i <= end; i++) {
                if (chunk.Offsets[i] != offset) continue;
                targetIdx = i;
                break;
            }
            if (targetIdx < 0) continue;

            var moveCount = chunk.Count - targetIdx - 1;
            if (moveCount > 0) {
                Array.Copy(chunk.Keys, targetIdx + 1, chunk.Keys, targetIdx, moveCount);
                Array.Copy(chunk.Offsets, targetIdx + 1, chunk.Offsets, targetIdx, moveCount);
            }

            chunk.Count--;
            Count--;

            if (chunk.Count == 0 && chunks.Count > 1) {
                chunks.RemoveAt(c);
            }
            return;
        }
    }

    protected override OffsetList ScanOffsets(
        IndexBound<TKey> from,
        IndexBound<TKey> to,
        FilterDescriptor<TKey>? filterParam = null
    ) {
        if (chunks.Count == 0 || Count == 0) return OffsetList.Empty();

        using var offsetBuilder = OffsetListBuilder.Create(Constants.OffsetBuilderInitialCapacity);
        var chunkSpan = CollectionsMarshal.AsSpan(chunks);
        var startChunk = from.IsBounded ? FindFirstChunkWithMaxKeyAtLeast(from.Key) : 0;

        for (var c = startChunk; c < chunks.Count; c++) {
            ref readonly var chunk = ref chunkSpan[c];

            if (to.IsBounded) {
                var minCmp = chunk.MinKey.CompareTo(to.Key);
                if (minCmp > 0 || (minCmp == 0 && !to.IsInclusive)) break;
            }

            var internalIdx = 0;
            if (from.IsBounded) {
                internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, from.Key);
                if (internalIdx < 0) {
                    internalIdx = ~internalIdx;
                } else {
                    while (internalIdx > 0 && chunk.Keys[internalIdx - 1].CompareTo(from.Key) == 0) internalIdx--;
                    if (!from.IsInclusive) {
                        while (internalIdx < chunk.Count && chunk.Keys[internalIdx].CompareTo(from.Key) == 0) internalIdx++;
                    }
                }
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
        ReadOnlySpan<IndexChunk> chunkSpan = CollectionsMarshal.AsSpan(chunks);
        for (var i = 0; i < chunkSpan.Length; i++) {
            ref readonly var chunk = ref chunkSpan[i];
            if (chunk.Count == 0 || chunk.MaxKey.CompareTo(key) >= 0 || i == chunkSpan.Length - 1) {
                return i;
            }
        }
        return 0;
    }

    private void SplitAndInsert(int chunkIdx, int internalIdx, TKey key, int offset) {
        ref var oldChunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
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
