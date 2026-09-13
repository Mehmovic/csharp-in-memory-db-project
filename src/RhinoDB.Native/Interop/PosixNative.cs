using System.Runtime.InteropServices;

namespace RhinoDB.Native.Interop;

static internal partial class PosixNative {
    private const string LibraryName = "libc";

    [LibraryImport(LibraryName, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    static public partial int open(string pathname, int flags);

    [LibraryImport(LibraryName, EntryPoint = "fsync", SetLastError = true)]
    static public partial int fsync(int fd);

    [LibraryImport(LibraryName, EntryPoint = "close", SetLastError = true)]
    static public partial int close(int fd);
}
