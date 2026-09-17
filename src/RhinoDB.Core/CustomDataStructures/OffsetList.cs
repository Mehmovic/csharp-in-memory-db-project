using System.Buffers;

using RhinoDB.Core.Exceptions;

namespace RhinoDB.Core;

public ref struct OffsetListBuilder : IDisposable {
    private int[] array;
    private Span<int> buffer;
    private int count;

    private OffsetListBuilder(int initialCapacity) {
        array = initialCapacity > 0 ? ArrayPool<int>.Shared.Rent(initialCapacity) : [];
        buffer = array;
        count = 0;
    }

    public Result Add(int offset) {
        if (IsDisposed()) return Result.Error(DbError.OffsetListDisposed());

        if (count >= buffer.Length) {
            Grow();
        }
        buffer[count++] = offset;
        return Result.Ok();
    }

    public StackResult<ReadOnlySpan<int>> BufferResult() {
        return IsDisposed()
            ? StackResult.Error(DbError.OffsetListDisposed())
            : StackResult<ReadOnlySpan<int>>.Ok(buffer[..count]);
    }

    public StackResult<OffsetList> Build() {
        if (IsDisposed()) return StackResult.Error(DbError.OffsetListDisposed());

        var frozen = new OffsetList(array, count);
        array = null!;
        buffer = null!;
        count = 0;
        return frozen;
    }

    public void Dispose() {
        if (IsDisposed()) return;
        if (array.Length > 0) {
            ArrayPool<int>.Shared.Return(array);
        }
        array = null!;
    }

    private bool IsDisposed() => array == null;

    private void Grow() {
        var length = int.Max(1, array.Length);
        var newArray = ArrayPool<int>.Shared.Rent(length * 2);

        if (array.Length > 0) {
            buffer[..count].CopyTo(newArray);
            ArrayPool<int>.Shared.Return(array);
        }

        array = newArray;
        buffer = array;
    }

    static public OffsetListBuilder Create(int capacity = 1) {
        return new OffsetListBuilder(capacity);
    }
    
    static public OffsetList Empty() {
        return OffsetList.Empty();
    }
}

public ref struct OffsetList : IDisposable {
    private int[] array;
    private Span<int> buffer;
    public readonly int Count;

    internal OffsetList(int[] rentedArray, int count) {
        array = rentedArray;
        buffer = rentedArray.AsSpan(0, count);
        Count = count;
    }

    static public OffsetList Empty() => new OffsetList([], 0);

    public bool IsEmpty() => Count == 0;

    public StackResult<ReadOnlySpan<int>> BufferResult() {
        return IsDisposed()
            ? StackResult.Error(DbError.OffsetListDisposed())
            : StackResult<ReadOnlySpan<int>>.Ok(buffer);
    }

    public ReadOnlySpan<int> Buffer() {
        return IsDisposed() ? throw new OffsetListDisposedException() : buffer;
    }

    public void Dispose() {
        if (IsDisposed()) return;
        
        if (array.Length > 0) {
            ArrayPool<int>.Shared.Return(array);
        }
        
        array = null!;
        buffer = default;
    }

    public readonly Enumerator GetEnumerator() => new Enumerator(buffer[..Count], Count);
    public ref struct Enumerator {
        private readonly ReadOnlySpan<int> span;
        private readonly int count;
        private int index;

        internal Enumerator(ReadOnlySpan<int> span, int count) {
            this.span = span;
            this.count = count;
            index = -1;
        }

        public bool MoveNext() {
            var next = index + 1;
            if (next >= count) return false;

            index = next;
            return true;
        }

        public readonly int Current => span[index];
    }

    private bool IsDisposed() => array == null;
}
