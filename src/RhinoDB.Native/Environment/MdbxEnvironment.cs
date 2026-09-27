using System.Runtime.InteropServices;
using RhinoDB.Native.Interop;

namespace RhinoDB.Native;

public sealed class MdbxEnvironment : IDisposable {
    private readonly nint handle;
    private bool disposed;

    private MdbxEnvironment(nint handle) => this.handle = handle;

    static public int Create(out MdbxEnvironment? environment) {
        var rc = MdbxNative.mdbx_env_create(out var handle);
        environment = rc == 0 ? new MdbxEnvironment(handle) : null;
        return rc;
    }

    public int SetMaxDbs(uint dbs) => MdbxNative.mdbx_env_set_maxdbs(handle, dbs);

    public int SetGeometry(nint sizeLower, nint sizeNow, nint sizeUpper, nint growthStep, nint shrinkThreshold, nint pagesize) =>
        MdbxNative.mdbx_env_set_geometry(handle, sizeLower, sizeNow, sizeUpper, growthStep, shrinkThreshold, pagesize);

    public int Open(string pathname, uint flags, ushort mode) => MdbxNative.mdbx_env_open(handle, pathname, flags, mode);

    public int Sync(bool force, bool nonblock) => MdbxNative.mdbx_env_sync_ex(handle, force, nonblock);

    public int BeginTxn(uint flags, out Transaction? txn) {
        var rc = MdbxNative.mdbx_txn_begin_ex(handle, 0, flags, out var txnHandle, 0);
        txn = rc == 0 ? new Transaction(txnHandle) : null;
        return rc;
    }

    static public string StrError(int code) {
        var ptr = MdbxNative.mdbx_strerror(code);
        return ptr == 0 ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        MdbxNative.mdbx_env_close_ex(handle, dontSync: false);
    }
}
