using System.Numerics;

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
        values[currentChunk][indexInChunk] = item;
        Count += 1;
        return index;
    }

    public T Get(int index) {
        var chunkIndex = index >> chunkShift;
        return values[chunkIndex][index & chunkMask];
    }

    public void Set(int index, T value) {
        var chunkIndex = index >> chunkShift;
        values[chunkIndex][index & chunkMask] = value;
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
        var lastItem = values[currentChunk][lastIndexInChunk];
        values[targetChunk][indexInChunk] = lastItem;

        Count -= 1;
        RemoveEmptyChunkIfRequired();
        MoveChunkCursorIfApplicable(lastIndexInChunk);
        return lastItem;
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
