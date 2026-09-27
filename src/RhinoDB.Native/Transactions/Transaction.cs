using RhinoDB.Native.Interop;

namespace RhinoDB.Native;

public sealed class Transaction : IDisposable {
    private readonly nint handle;
    private bool completed;

    internal Transaction(nint handle) => this.handle = handle;

    public int OpenDbi(string? name, uint flags, out uint dbi) => MdbxNative.mdbx_dbi_open(handle, name, flags, out dbi);

    public int Get(uint dbi, ReadOnlySpan<byte> key, out byte[] value) => MdbxValMarshal.Get(handle, dbi, key, out value);

    public int Put(uint dbi, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, uint flags) =>
        MdbxValMarshal.Put(handle, dbi, key, value, flags);

    public int Delete(uint dbi, ReadOnlySpan<byte> key) => MdbxValMarshal.Del(handle, dbi, key);

    public int Drop(uint dbi, bool del = true) => MdbxNative.mdbx_drop(handle, dbi, del);

    public int OpenCursor(uint dbi, out Cursor? cursor) {
        var rc = MdbxNative.mdbx_cursor_open(handle, dbi, out var cursorHandle);
        cursor = rc == 0 ? new Cursor(cursorHandle) : null;
        return rc;
    }

    public int Commit() {
        completed = true;
        return MdbxNative.mdbx_txn_commit(handle);
    }

    public int Abort() {
        completed = true;
        return MdbxNative.mdbx_txn_abort(handle);
    }

    public void Dispose() {
        if (!completed) Abort();
    }
}
