using System.Runtime.InteropServices;

namespace RhinoDB.Native.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct MdbxVal {
    public nint Data;
    public nuint Length;
}
