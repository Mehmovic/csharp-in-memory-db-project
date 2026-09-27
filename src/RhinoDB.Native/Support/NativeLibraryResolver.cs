using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RhinoDB.Native;

static internal class NativeLibraryResolver {
    [ModuleInitializer]
    static internal void Register() {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    static private nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (libraryName != "mdbx") return nint.Zero;

        var (rid, fileName) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ("win-x64", "mdbx.dll")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64", "libmdbx.dylib")
            : ("linux-x64", "libmdbx.so");

        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);
        return NativeLibrary.TryLoad(path, out var handle) ? handle : nint.Zero;
    }
}
