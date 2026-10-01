namespace RhinoDB.Lib.Changes;

public sealed class LsnSequence(ulong seed = 0) {
    private ulong current = seed;

    public ulong Next() => ++current;
}