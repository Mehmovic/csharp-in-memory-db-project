using System.Runtime.InteropServices;
using RhinoDB.Native.Interop;

namespace RhinoDB.Native;

static public class DirectorySync {
    private const int ORdOnly = 0;

    static public bool TrySync(string path, out int errno) {
        if (OperatingSystem.IsWindows()) {
            errno = 0;
            return true;
        }

        var fd = PosixNative.open(path, ORdOnly);
        if (fd < 0) {
            errno = Marshal.GetLastPInvokeError();
            return false;
        }

        var rc = PosixNative.fsync(fd);
        errno = rc == 0 ? 0 : Marshal.GetLastPInvokeError();
        PosixNative.close(fd);
        return rc == 0;
    }
}
