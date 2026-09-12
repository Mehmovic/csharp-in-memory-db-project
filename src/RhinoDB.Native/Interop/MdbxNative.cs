using System.Runtime.InteropServices;

namespace RhinoDB.Native.Interop;

static internal partial class MdbxNative {
    private const string LibraryName = "mdbx";

    [LibraryImport(LibraryName)]
    static public partial int mdbx_env_create(out nint env);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_env_set_maxdbs(nint env, uint dbs);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_env_set_geometry(
        nint env, nint sizeLower, nint sizeNow, nint sizeUpper, nint growthStep, nint shrinkThreshold, nint pagesize);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    static public partial int mdbx_env_open(nint env, string pathname, uint flags, ushort mode);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_env_close_ex(nint env, [MarshalAs(UnmanagedType.U1)] bool dontSync);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_env_sync_ex(
        nint env, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool nonblock);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_txn_begin_ex(nint env, nint parent, uint flags, out nint txn, nint context);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_txn_commit(nint txn);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_txn_abort(nint txn);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    static public partial int mdbx_dbi_open(nint txn, string? name, uint flags, out uint dbi);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_get(nint txn, uint dbi, in MdbxVal key, out MdbxVal data);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_put(nint txn, uint dbi, in MdbxVal key, ref MdbxVal data, uint flags);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_del(nint txn, uint dbi, in MdbxVal key, nint data);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_cursor_open(nint txn, uint dbi, out nint cursor);

    [LibraryImport(LibraryName)]
    static public partial void mdbx_cursor_close(nint cursor);

    [LibraryImport(LibraryName)]
    static public partial int mdbx_cursor_get(nint cursor, ref MdbxVal key, ref MdbxVal data, int op);

    [LibraryImport(LibraryName)]
    static public partial nint mdbx_strerror(int errnum);
}
