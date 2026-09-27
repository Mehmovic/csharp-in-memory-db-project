using RhinoDB.Native.Interop;

namespace RhinoDB.Native;

public sealed class Cursor : IDisposable {
    private readonly nint handle;
    private bool disposed;

    internal Cursor(nint handle) => this.handle = handle;

    public int GetFirst(out byte[] key, out byte[] value) => MdbxValMarshal.CursorGetFirst(handle, out key, out value);
    public int GetNext(out byte[] key, out byte[] value) => MdbxValMarshal.CursorGetNext(handle, out key, out value);

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        MdbxNative.mdbx_cursor_close(handle);
    }
}
