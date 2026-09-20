using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RhinoDB.Lib.Storage;

public class DenseArray<T>
    where T : struct {
    private readonly int chunkSize;
    private readonly int chunkShift;
    private readonly int chunkMask;
    private readonly List<T[]> values;
    private int currentChunk;
    private int chunkCount;

    public DenseArray(int chunkSize) {
        this.chunkSize = (int)BitOperations.RoundUpToPowerOf2((uint)chunkSize);
        chunkShift = BitOperations.Log2((uint)this.chunkSize);
        chunkMask = this.chunkSize - 1;
        values = [new T[this.chunkSize]];
        currentChunk = 0;
        chunkCount = 1;
        Count = 0;
    }

    public int Count { get; private set; }
    public int LastOffset => Count - 1;
    public int Capacity => chunkCount * chunkSize;

    public int Insert(T item) {
        var index = Count;

        if (Count != 0 && (Count & chunkMask) == 0) {
            values.Add(new T[chunkSize]);
            chunkCount += 1;
            currentChunk += 1;
        }

        var indexInChunk = index & chunkMask;
        CollectionsMarshal.AsSpan(values)[currentChunk][indexInChunk] = item;
        Count += 1;
        return index;
    }

    public T Get(int index) {
        var chunkIndex = index >> chunkShift;
        return CollectionsMarshal.AsSpan(values)[chunkIndex][index & chunkMask];
    }

    public void Set(int index, T value) {
        var chunkIndex = index >> chunkShift;
        CollectionsMarshal.AsSpan(values)[chunkIndex][index & chunkMask] = value;
    }

    public T? Delete(int index) {
        var lastIndex = Count - 1;
        var lastIndexInChunk = lastIndex & chunkMask;

        if (index == lastIndex) {
            Count -= 1;
            RemoveEmptyChunkIfRequired();
            MoveChunkCursorIfApplicable(lastIndexInChunk);
            return null;
        }

        var targetChunk = index >> chunkShift;
        var indexInChunk = index & chunkMask;
        var chunkSpan = CollectionsMarshal.AsSpan(values);
        var lastItem = chunkSpan[currentChunk][lastIndexInChunk];
        chunkSpan[targetChunk][indexInChunk] = lastItem;

        Count -= 1;
        RemoveEmptyChunkIfRequired();
        MoveChunkCursorIfApplicable(lastIndexInChunk);
        return lastItem;
    }

    public StackArrayPoolContainer<DenseArrayRelocation<T>> DeleteMany(ReadOnlySpan<int> offsets) {
        if (offsets.Length == 0) return StackArrayPoolContainer<DenseArrayRelocation<T>>.Empty();

        var targetsArray = ArrayPool<int>.Shared.Rent(offsets.Length);
        try {
            var targets = targetsArray.AsSpan(0, offsets.Length);
            offsets.CopyTo(targets);
            targets.Sort();

            var newCount = Count - targets.Length;
            var lo = 0;
            var hi = targets.Length - 1;
            var src = Count - 1;

            using var relocationBuilder = StackArrayPoolContainerBuilder<DenseArrayRelocation<T>>.Create(targets.Length);

            while (lo < targets.Length && targets[lo] < newCount) {
                while (src >= newCount && hi >= 0 && src == targets[hi]) {
                    hi -= 1;
                    src -= 1;
                }

                var moved = Get(src);
                Set(targets[lo], moved);
                relocationBuilder.Add(new DenseArrayRelocation<T>(moved, src, targets[lo]));
                lo += 1;
                src -= 1;
            }

            Count = newCount;
            TrimChunksTo(newCount);

            return relocationBuilder.Build().Unwrap();
        } finally {
            ArrayPool<int>.Shared.Return(targetsArray);
        }
    }

    private void TrimChunksTo(int newCount) {
        var requiredChunks = newCount == 0 ? 1 : ((newCount - 1) >> chunkShift) + 1;
        if (requiredChunks < chunkCount) {
            values.RemoveRange(requiredChunks, chunkCount - requiredChunks);
            chunkCount = requiredChunks;
        }

        currentChunk = chunkCount - 1;
    }

    private void MoveChunkCursorIfApplicable(int lastIndexInChunk) {
        if (lastIndexInChunk == 0 && currentChunk > 0) {
            currentChunk -= 1;
        }
    }

    private void RemoveEmptyChunkIfRequired() {
        if ((Count & chunkMask) == chunkSize >> 1 && chunkCount >= currentChunk + 2) {
            values.RemoveAt(currentChunk + 1);
            chunkCount -= 1;
        }
    }
}

public readonly record struct DenseArrayRelocation<T>(T Row, int OldOffset, int NewOffset) where T : struct;
