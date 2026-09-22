namespace RhinoDB.Lib.Storage;

public ref struct StorageRefEnumerator<TRow>
    where TRow : struct {
    private readonly DenseArray<TRow> storage;
    private int index;

    internal StorageRefEnumerator(DenseArray<TRow> storage) {
        this.storage = storage;
        index = -1;
    }


    public readonly int Count => storage.Count;

    public bool MoveNext() {
        var next = index + 1;
        if (next >= Count) return false;

        index = next;
        return true;
    }

    public readonly ref readonly TRow Current => ref storage.GetRef(index);

    public readonly StorageRefEnumerator<TRow> GetEnumerator()
        => new StorageRefEnumerator<TRow>(storage);
}

public ref struct StorageOffsetRefEnumerator<TRow>
    where TRow : struct {
    private readonly DenseArray<TRow> storage;
    private readonly ReadOnlySpan<int> offsets;
    private int index;

    internal StorageOffsetRefEnumerator(DenseArray<TRow> storage, ReadOnlySpan<int> offsets) {
        this.storage = storage;
        this.offsets = offsets;
        index = -1;
    }

    public readonly int Count => offsets.Length;

    public bool MoveNext() {
        var next = index + 1;
        if (next >= Count) return false;

        index = next;
        return true;
    }

    public readonly ref readonly TRow Current => ref storage.GetRef(offsets[index]);

    public readonly StorageOffsetRefEnumerator<TRow> GetEnumerator()
        => new StorageOffsetRefEnumerator<TRow>(storage, offsets);
}
