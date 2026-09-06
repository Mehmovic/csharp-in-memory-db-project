using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RhinoDB.Native;

// .NET's default P/Invoke probing only walks into runtimes/{rid}/native/
// automatically when the consuming app was restored with RID-aware asset
// resolution (a real NuGet package reference, or an explicit
// RuntimeIdentifier at publish time). A plain ProjectReference - which is
// all RhinoDB.Lib/the test projects use today - copies mdbx.dll/libmdbx.so
// there via this project's .csproj, but the loader never looks: this
// resolver is what actually points "mdbx" at the right file.
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
