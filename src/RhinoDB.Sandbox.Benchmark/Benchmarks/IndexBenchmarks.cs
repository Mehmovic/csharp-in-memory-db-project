using BenchmarkDotNet.Attributes;

using RhinoDB.Lib.Indexing;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

// Baseline for the ordered-index rework: chunk bounds cached as fields, a comparer
// seam (string keys go ordinal), pooled chunk storage, the scan buffer pre-sized when
// the result length is known upfront, and loop-invariant branches hoisted.
//
// Every method that reads state first runs under a global setup that also asserts the
// index returns the expected number of offsets - a structurally wrong index used to time
// perfectly while returning the wrong window.
[MemoryDiagnoser]
public class IndexBenchmarks {
    private const int Rows = 100_000;
    private const int BulkRows = 1_000_000;

    private BTreeIndex<int> intIndex = null!;
    private BTreeIndex<string> strIndex = null!;
    private int probe;
    private string strProbe = null!;

    [GlobalSetup]
    public void Setup() {
        intIndex = new BTreeIndex<int>();
        strIndex = new BTreeIndex<string>(256, StringComparer.Ordinal);
        for (var i = 0; i < Rows; i++) {
            intIndex.Insert(i, i);
            strIndex.Insert(Key(i), i);
        }

        // 1M rows is ~3900 chunks, so the start-chunk binary search is a real cost here
        // rather than a couple of probes.
        probe = Rows / 2;
        strProbe = Key(probe);

        AssertRange(intIndex.GetOffset(probe).IsOk(), "int point lookup must resolve");
        AssertRange(strIndex.GetOffset(strProbe).IsOk(), "string point lookup must resolve");
        AssertRange(intIndex.GetOffsetsRange(probe, probe + 99).Count == 100, "width-100 range must return 100");
        AssertRange(intIndex.GetOffsetsGt(Rows - 1).Count == 0, "Gt past the last key must return none");
    }

    static private string Key(int i) => "player-" + i.ToString("D9");

    static private void AssertRange(bool condition, string message) {
        if (!condition) throw new InvalidOperationException("index is structurally wrong: " + message);
    }

    [Benchmark(Baseline = true)]
    public int Lookup_Int() => intIndex.GetOffset(probe).Unwrap();

    [Benchmark]
    public int Lookup_String() => strIndex.GetOffset(strProbe).Unwrap();

    [Benchmark]
    public int Range_Width1() => intIndex.GetOffsetsRange(probe, probe).Count;

    [Benchmark]
    public int Range_Width100() => intIndex.GetOffsetsRange(probe, probe + 99).Count;

    [Benchmark]
    public int Range_Width10000() => intIndex.GetOffsetsRange(probe, probe + 9999).Count;

    // The generator's Iter() path: unbounded/unbounded, so the result length is known
    // before a single element is read.
    [Benchmark]
    public int Scan_All() => intIndex.GetOffsetsIter().Count;

    [Benchmark]
    public int Scan_Except() => intIndex.GetOffsetsExcept(probe).Count;

    // Bulk load: dominated by splits, so it shows chunk allocation cost directly.
    [Benchmark]
    public int BulkLoad_1M() {
        var index = new BTreeIndex<int>();
        for (var i = 0; i < BulkRows; i++) index.Insert(i, i);
        return index.Count;
    }

    [Benchmark]
    public int DeleteHalf_ThenInsertHalf() {
        var index = new BTreeIndex<int>();
        for (var i = 0; i < Rows; i++) index.Insert(i, i);
        for (var i = 0; i < Rows; i += 2) index.Delete(i);
        for (var i = 0; i < Rows; i++) index.Insert(i, i);
        return index.Count;
    }
}