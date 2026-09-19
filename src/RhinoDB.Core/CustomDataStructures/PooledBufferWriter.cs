using System.Buffers;

namespace RhinoDB.Core;

public struct PooledBufferWriter(int initialCapacity) : IBufferWriter<byte>, IDisposable {
    private byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 16));
    private int written;

    public readonly ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, written);

    public void Advance(int count) => written += count;

    public Memory<byte> GetMemory(int sizeHint = 0) {
        EnsureCapacity(sizeHint);
        return buffer.AsMemory(written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) {
        EnsureCapacity(sizeHint);
        return buffer.AsSpan(written);
    }

    private void EnsureCapacity(int sizeHint) {
        var needed = Math.Max(sizeHint, 1);
        if (buffer.Length - written >= needed) return;

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(buffer.Length * 2, written + needed));
        buffer.AsSpan(0, written).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = grown;
    }

    public void Dispose() {
        if (buffer == null) return;
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = null!;
    }
}
