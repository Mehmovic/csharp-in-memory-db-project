namespace RhinoDB.Lib.Indexing;

// used in the TableGenerator.cs
static public class OrdinalComparers {
    static private IComparer<T> Element<T>()
        => typeof(T) == typeof(string)
            ? (IComparer<T>)StringComparer.Ordinal
            : Comparer<T>.Default;

    static public IComparer<(T1 Item1, T2 Item2)> For2<T1, T2>()
        => new TupleComparer<T1, T2>(Element<T1>(), Element<T2>());

    static public IComparer<(T1 Item1, T2 Item2, T3 Item3)> For3<T1, T2, T3>()
        => new TupleComparer<T1, T2, T3>(Element<T1>(), Element<T2>(), Element<T3>());
}

file sealed class TupleComparer<T1, T2>(IComparer<T1> c1, IComparer<T2> c2) : IComparer<(T1 Item1, T2 Item2)> {
    public int Compare((T1 Item1, T2 Item2) x, (T1 Item1, T2 Item2) y) {
        var c = c1.Compare(x.Item1, y.Item1);
        return c != 0 ? c : c2.Compare(x.Item2, y.Item2);
    }
}

file sealed class TupleComparer<T1, T2, T3>(IComparer<T1> c1, IComparer<T2> c2, IComparer<T3> c3) : IComparer<(T1 Item1, T2 Item2, T3 Item3)> {
    public int Compare((T1 Item1, T2 Item2, T3 Item3) x, (T1 Item1, T2 Item2, T3 Item3) y) {
        var c = c1.Compare(x.Item1, y.Item1);
        if (c != 0) return c;
        c = c2.Compare(x.Item2, y.Item2);
        return c != 0 ? c : c3.Compare(x.Item3, y.Item3);
    }
}