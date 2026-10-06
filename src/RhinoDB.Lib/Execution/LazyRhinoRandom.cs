using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace RhinoDB.Lib.Execution;

public struct LazyRhinoRandom {
    private RhinoRandom random;
    private ulong seed;
    private bool seeded;

    [UnscopedRef]
    public ref RhinoRandom Value {
        get {
            if (seeded) return ref random;
            
            seed = NewSeed();
            random = new RhinoRandom(seed);
            seeded = true;
            return ref random;
        }
    }

    internal ulong? Seed => seeded ? seed : null;

    static private ulong NewSeed() {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}
