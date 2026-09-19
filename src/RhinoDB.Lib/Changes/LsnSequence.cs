namespace RhinoDB.Lib.Changes;

public sealed class LsnSequence(long seed = 0) {
    private long current = seed;

    public long Next() => ++current;
}
