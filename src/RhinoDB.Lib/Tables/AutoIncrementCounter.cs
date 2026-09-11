namespace RhinoDB.Lib.Tables;

public sealed class AutoIncrementCounter(long start = 1) {
    private long next = start;

    public long Next() => next++;
}
