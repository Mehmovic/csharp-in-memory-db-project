using System.Buffers;
using System.Runtime.CompilerServices;

using RhinoDB.Core.Exceptions;

namespace RhinoDB.Core;

public struct ArrayPoolContainerBuilder<T> : IDisposable {
    private T[] array;
    private Memory<T> buffer;
    private int count;

    private ArrayPoolContainerBuilder(int initialCapacity) {
        array = initialCapacity > 0 ? ArrayPool<T>.Shared.Rent(initialCapacity) : [];
        buffer = array;
        count = 0;
    }

    public Result Add(T offset) {
        if (IsDisposed()) return Result.Error(DbError.ArrayPoolDisposed());

        if (count >= buffer.Length) {
            Grow();
        }
        buffer.Span[count++] = offset;
        return Result.Ok();
    }

    public StackResult<ReadOnlySpan<T>> BufferResult() {
        return IsDisposed()
            ? StackResult.Error(DbError.ArrayPoolDisposed())
            : StackResult<ReadOnlySpan<T>>.Ok(buffer[..count].Span);
    }

    public StackResult<ArrayPoolContainer<T>> Build() {
        if (IsDisposed()) return StackResult.Error(DbError.ArrayPoolDisposed());

        var frozen = new ArrayPoolContainer<T>(array, count);
        array = null!;
        buffer = null!;
        count = 0;
        return frozen;
    }

    public void Dispose() {
        if (IsDisposed()) return;
        if (array.Length > 0) {
            ArrayPool<T>.Shared.Return(array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
        array = null!;
    }

    private bool IsDisposed() => array == null;

    private void Grow() {
        var length = int.Max(1, array.Length);
        var newArray = ArrayPool<T>.Shared.Rent(length * 2);

        if (array.Length > 0) {
            buffer[..count].CopyTo(newArray);
            ArrayPool<T>.Shared.Return(array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }

        array = newArray;
        buffer = array;
    }

    static public ArrayPoolContainerBuilder<T> Create(int capacity = 1) {
        return new ArrayPoolContainerBuilder<T>(capacity);
    }

    static public ArrayPoolContainer<T> Empty() {
        return ArrayPoolContainer<T>.Empty();
    }
}

public struct ArrayPoolContainer<T> : IDisposable {
    private T[] array;
    private Memory<T> buffer;
    public readonly int Count;

    internal ArrayPoolContainer(T[] rentedArray, int count) {
        array = rentedArray;
        buffer = rentedArray.AsMemory()[..count];
        Count = count;
    }

    static public ArrayPoolContainer<T> Empty() => new ArrayPoolContainer<T>([], 0);

    public bool IsEmpty() => Count == 0;

    public StackResult<ReadOnlySpan<T>> BufferResult() {
        return IsDisposed()
            ? StackResult.Error(DbError.ArrayPoolDisposed())
            : StackResult<ReadOnlySpan<T>>.Ok(buffer.Span);
    }

    public readonly ReadOnlySpan<T> Buffer() {
        return array == null ? throw new ArrayPoolDisposedException() : buffer.Span;
    }

    public void Dispose() {
        if (IsDisposed()) return;

        if (array.Length > 0) {
            ArrayPool<T>.Shared.Return(array, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }

        array = null!;
        buffer = default;
    }

    public readonly Enumerator GetEnumerator() => new Enumerator(buffer[..Count].Span, Count);
    public readonly RefEnumerator GetRefEnumerator() => new RefEnumerator(buffer[..Count].Span, Count);

    public ref struct Enumerator {
        private readonly ReadOnlySpan<T> span;
        private readonly int count;
        private int index;

        internal Enumerator(ReadOnlySpan<T> span, int count) {
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

        public readonly T Current => span[index];
    }
    
    public ref struct RefEnumerator {
        private readonly ReadOnlySpan<T> span;
        private readonly int count;
        private int index;

        internal RefEnumerator(ReadOnlySpan<T> span, int count) {
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

        public readonly ref readonly T Current => ref span[index];
    }

    private bool IsDisposed() => array == null;
}
