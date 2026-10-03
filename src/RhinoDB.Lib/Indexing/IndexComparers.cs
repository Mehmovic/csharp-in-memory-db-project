using System.Runtime.CompilerServices;

namespace RhinoDB.Lib.Indexing;

public readonly struct DefaultComparer<T> : IComparer<T> {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(T? x, T? y) => Comparer<T>.Default.Compare(x, y);
}

public readonly struct OrdinalStringComparer : IComparer<string> {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(string? x, string? y) => string.CompareOrdinal(x, y);
}

public readonly struct TupleComparer<T1, T2, TCmp1, TCmp2> : IComparer<(T1 Item1, T2 Item2)>
    where TCmp1 : struct, IComparer<T1>
    where TCmp2 : struct, IComparer<T2> {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare((T1 Item1, T2 Item2) x, (T1 Item1, T2 Item2) y) {
        var c = default(TCmp1).Compare(x.Item1, y.Item1);
        return c != 0 ? c : default(TCmp2).Compare(x.Item2, y.Item2);
    }
}

public readonly struct TupleComparer<T1, T2, T3, TCmp1, TCmp2, TCmp3> : IComparer<(T1 Item1, T2 Item2, T3 Item3)>
    where TCmp1 : struct, IComparer<T1>
    where TCmp2 : struct, IComparer<T2>
    where TCmp3 : struct, IComparer<T3> {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare((T1 Item1, T2 Item2, T3 Item3) x, (T1 Item1, T2 Item2, T3 Item3) y) {
        var c = default(TCmp1).Compare(x.Item1, y.Item1);
        if (c != 0) return c;
        c = default(TCmp2).Compare(x.Item2, y.Item2);
        return c != 0 ? c : default(TCmp3).Compare(x.Item3, y.Item3);
    }
}
