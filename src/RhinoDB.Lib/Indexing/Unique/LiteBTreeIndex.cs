using System.Numerics;
using System.Runtime.InteropServices;

namespace RhinoDB.Lib.Indexing;

public class LiteBTreeIndex<TKey> where TKey : IComparable<TKey> {
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

    public LiteBTreeIndex(int chunkSize = 256) {
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
            SplitAndInsert(ref chunkIdx, ref internalIdx, key, offset);
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

    public void Range(TKey from, TKey to, ICollection<int> into) {
        if (chunks.Count == 0 || Count == 0) return;

        var startChunkIdx = 0;
        var chunkSpan = CollectionsMarshal.AsSpan(chunks);
        while (startChunkIdx < chunks.Count && chunkSpan[startChunkIdx].MaxKey.CompareTo(from) < 0) {
            startChunkIdx++;
        }

        for (var c = startChunkIdx; c < chunks.Count; c++) {
            ref readonly IndexChunk chunk = ref chunkSpan[c];

            if (chunk.MinKey.CompareTo(to) > 0) break;

            for (var i = 0; i < chunk.Count; i++) {
                TKey currentKey = chunk.Keys[i];

                var minCompare = currentKey.CompareTo(from);
                var maxCompare = currentKey.CompareTo(to);

                if (minCompare >= 0 && maxCompare <= 0) {
                    into.Add(chunk.Offsets[i]);
                } else if (maxCompare > 0) {
                    return;
                }
            }
        }
    }

    private int FindTargetChunkForInsertion(TKey key) {
        ReadOnlySpan<IndexChunk> chunkSpan = CollectionsMarshal.AsSpan(chunks);
        for (var i = 0; i < chunkSpan.Length; i++) {
            ref readonly IndexChunk chunk = ref chunkSpan[i];
            if (chunk.Count == 0 || chunk.MaxKey.CompareTo(key) >= 0 || i == chunkSpan.Length - 1) {
                return i;
            }
        }
        return 0;
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

    private void SplitAndInsert(ref int chunkIdx, ref int internalIdx, TKey key, int offset) {
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

            chunkIdx++;
        }

        chunks.Insert(chunkIdx, newChunk);
    }
}