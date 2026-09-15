using System.Numerics;
using System.Runtime.InteropServices;

namespace RhinoDB.Lib.Indexing;

public class NonUniqueLiteBTreeIndex<TKey> where TKey : IComparable<TKey> {
    private struct IndexChunk(int capacity) {
        public readonly TKey[] Keys = new TKey[capacity];
        public readonly int[] Offsets = new int[capacity];
        public int Count = 0;

        public readonly TKey MinKey => Keys[0];
        public readonly TKey MaxKey => Keys[Count - 1];
        public readonly bool IsFull => Count == Keys.Length;
    }

    private readonly int chunkCapacity;
    private readonly List<IndexChunk> chunks;

    public int Count { get; private set; }

    public NonUniqueLiteBTreeIndex(int chunkSize = 256) {
        chunkCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, chunkSize));
        chunks = [new IndexChunk(chunkCapacity)];
        Count = 0;
    }
    
    public void GetOffsets(TKey key, ICollection<int> into) {
        var chunkIdx = FindChunkContainingKey(key);
        if (chunkIdx < 0) return;

        ref readonly IndexChunk chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key);
        if (internalIdx < 0) return;

        var start = internalIdx;
        while (start > 0 && chunk.Keys[start - 1].CompareTo(key) == 0) {
            start--;
        }

        for (var i = start; i < chunk.Count && chunk.Keys[i].CompareTo(key) == 0; i++) {
            into.Add(chunk.Offsets[i]);
        }
    }

    public void Insert(TKey key, int offset) {
        var chunkIdx = FindTargetChunkForInsertion(key);
        ref IndexChunk chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];

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

    public void Delete(TKey key, int offset) {
        var chunkIdx = FindChunkContainingKey(key);
        if (chunkIdx < 0) return;

        ref IndexChunk chunk = ref CollectionsMarshal.AsSpan(chunks)[chunkIdx];
        var internalIdx = Array.BinarySearch(chunk.Keys, 0, chunk.Count, key);
        if (internalIdx < 0) return;

        var targetIdx = -1;
        var scan = internalIdx;
        while (scan >= 0 && chunk.Keys[scan].CompareTo(key) == 0) {
            if (chunk.Offsets[scan] == offset) {
                targetIdx = scan;
                break;
            }
            scan--;
        }
        if (targetIdx < 0) {
            scan = internalIdx + 1;
            while (scan < chunk.Count && chunk.Keys[scan].CompareTo(key) == 0) {
                if (chunk.Offsets[scan] == offset) {
                    targetIdx = scan;
                    break;
                }
                scan++;
            }
        }

        if (targetIdx < 0) return;

        var moveCount = chunk.Count - targetIdx - 1;
        if (moveCount > 0) {
            Array.Copy(chunk.Keys, targetIdx + 1, chunk.Keys, targetIdx, moveCount);
            Array.Copy(chunk.Offsets, targetIdx + 1, chunk.Offsets, targetIdx, moveCount);
        }

        chunk.Count--;
        Count--;

        if (chunk.Count == 0 && chunks.Count > 1) {
            chunks.RemoveAt(chunkIdx);
        }
    }

    public void Range(TKey from, TKey to, ICollection<int> resultOffsets) {
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
                    resultOffsets.Add(chunk.Offsets[i]);
                } else if (maxCompare > 0) {
                    return;
                }
            }
        }
    }

    private int FindTargetChunkForInsertion(TKey key) {
        ReadOnlySpan<IndexChunk> chunkSpan = CollectionsMarshal.AsSpan(chunks);
        for (var i = 0; i < chunkSpan.Length; i++) {
            if (chunkSpan[i].MaxKey.CompareTo(key) >= 0 || i == chunkSpan.Length - 1) {
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
            var mid = low + ((high - low) >> 1);
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