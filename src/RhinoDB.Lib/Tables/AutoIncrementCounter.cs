namespace RhinoDB.Lib.Tables;

public sealed class AutoIncrementCounter(long start = 1) {
    private long next = start;

    public long Next() => next++;

    public void Seed(long minNext) {
        if (minNext > next) next = minNext;
    }
}
