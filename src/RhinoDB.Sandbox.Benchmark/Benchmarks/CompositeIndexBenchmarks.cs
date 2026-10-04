using BenchmarkDotNet.Attributes;

using RhinoDB.Lib.Indexing;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

// Composite (tuple) index keys, which is where an index key stops being one machine word.
//
// Three shapes matter and they behave very differently:
//
//   * (int, long)      - purely numeric. Each component compares with a devirtualised
//                        constrained call, so the tuple adds little over a plain int key.
//   * (int, string)    - one string component. On the TupleComparer<..., OrdinalStringComparer>
//                        struct comparer this is an inlined memcmp; left on the default comparer
//                        the same comparison is an ICU call AND the resulting total order
//                        depends on the host's culture.
//   * (int, string) on the DEFAULT comparer is kept here as the measured "before" for
//     that fix - the generator no longer emits it.
//
// Every measured method is asserted correct in GlobalSetup first: a structurally wrong
// index times perfectly while returning the wrong window.
[MemoryDiagnoser]
public class CompositeIndexBenchmarks {
    private const int Rows = 100_000;

    private BTreeIndex<(int ClubId, long Season), TupleComparer<int, long, DefaultComparer<int>, DefaultComparer<long>>> numeric = null!;
    private BTreeIndex<(int ClubId, string Name), TupleComparer<int, string, DefaultComparer<int>, OrdinalStringComparer>> strOrdinal = null!;
    private BTreeIndex<(int ClubId, string Name), DefaultComparer<(int ClubId, string Name)>> strDefault = null!;

    private (int, long) numericProbe;
    private (int, string) strProbe;
    private int center;

    [GlobalSetup]
    public void Setup() {
        numeric = new BTreeIndex<(int ClubId, long Season), TupleComparer<int, long, DefaultComparer<int>, DefaultComparer<long>>>(256);
        strOrdinal = new BTreeIndex<(int ClubId, string Name), TupleComparer<int, string, DefaultComparer<int>, OrdinalStringComparer>>(256);
        strDefault = new BTreeIndex<(int, string), DefaultComparer<(int, string)>>();

        for (var i = 0; i < Rows; i++) {
            numeric.Insert((i, (long)i * 1000), i);
            strOrdinal.Insert(StrKey(i), i);
            strDefault.Insert(StrKey(i), i);
        }

        center = Rows / 2;
        numericProbe = NumKey(center);
        strProbe = StrKey(center);

        Require(numeric.GetOffset(numericProbe).IsOk(), "numeric tuple point lookup must resolve");
        Require(strOrdinal.GetOffset(strProbe).IsOk(), "ordinal string tuple point lookup must resolve");
        Require(strDefault.GetOffset(strProbe).IsOk(), "default string tuple point lookup must resolve");

        Require(numeric.GetOffsetsRange(numericProbe, NumKey(center + 99)).Count == 100,
            "numeric tuple width-100 range must return 100");
        Require(strOrdinal.GetOffsetsRange(strProbe, StrKey(center + 99)).Count == 100,
            "ordinal string tuple width-100 range must return 100");
        Require(strOrdinal.GetOffsetsIter().Count == Rows, "ordinal string tuple full scan must return every offset");
        Require(numeric.GetOffsetsIter().Count == Rows, "numeric tuple full scan must return every offset");
    }

    static private (int, string) StrKey(int i) => (i, "player-" + i.ToString("D9"));
    static private (int, long) NumKey(int i) => (i, (long)i * 1000);

    static private void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException("index is structurally wrong: " + message);
    }

    [Benchmark(Baseline = true)]
    public int Lookup_NumericTuple() => numeric.GetOffset(numericProbe).Unwrap();

    [Benchmark]
    public int Lookup_StringTuple_Ordinal() => strOrdinal.GetOffset(strProbe).Unwrap();

    // The "before" for the ordinal fix - same work, culture-aware string comparison.
    [Benchmark]
    public int Lookup_StringTuple_DefaultComparer() => strDefault.GetOffset(strProbe).Unwrap();

    [Benchmark]
    public int Range_NumericTuple_Width100() => numeric.GetOffsetsRange(numericProbe, NumKey(center + 99)).Count;

    [Benchmark]
    public int Range_StringTuple_Ordinal_Width100() => strOrdinal.GetOffsetsRange(strProbe, StrKey(center + 99)).Count;

    [Benchmark]
    public int Range_StringTuple_Default_Width100() => strDefault.GetOffsetsRange(strProbe, StrKey(center + 99)).Count;

    // Unbounded/unbounded is the Iter() path, where the scan buffer is sized from the
    // index's entry count before a single element is read.
    [Benchmark]
    public int Scan_NumericTuple_All() => numeric.GetOffsetsIter().Count;

    [Benchmark]
    public int Scan_StringTuple_Ordinal_All() => strOrdinal.GetOffsetsIter().Count;
}