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

    private BTreeIndex<int, DefaultComparer<int>> intIndex = null!;
    private BTreeIndex<string, OrdinalStringComparer> strIndex = null!;
    private int probe;
    private string strProbe = null!;

    // A fixed probe measures one lucky path: Rows / 2 used to be the exact middle chunk of the
    // old directory search, so it resolved on the first probe and hid the cost every other key
    // pays. The random probes walk a pre-shuffled key list instead, so each call lands in a
    // different chunk the way real lookups do (and pays the cache misses real lookups pay).
    private const int RandomProbeCount = 4_096;
    private int[] randomProbes = null!;
    private string[] randomStrProbes = null!;
    private int nextProbe;

    [GlobalSetup]
    public void Setup() {
        intIndex = new BTreeIndex<int, DefaultComparer<int>>();
        strIndex = new BTreeIndex<string, OrdinalStringComparer>(256);
        for (var i = 0; i < Rows; i++) {
            intIndex.Insert(i, i);
            strIndex.Insert(Key(i), i);
        }

        // 1M rows is ~3900 chunks, so the start-chunk binary search is a real cost here
        // rather than a couple of probes.
        probe = Rows / 2;
        strProbe = Key(probe);

        var rng = new Random(12_345);
        randomProbes = new int[RandomProbeCount];
        randomStrProbes = new string[RandomProbeCount];
        for (var i = 0; i < RandomProbeCount; i++) {
            randomProbes[i] = rng.Next(Rows);
            randomStrProbes[i] = Key(randomProbes[i]);
        }

        AssertRange(intIndex.GetOffset(probe).IsOk(), "int point lookup must resolve");
        AssertRange(strIndex.GetOffset(strProbe).IsOk(), "string point lookup must resolve");
        AssertRange(intIndex.GetOffsetsRange(probe, probe + 99).Count == 100, "width-100 range must return 100");
        AssertRange(intIndex.GetOffsetsGt(Rows - 1).Count == 0, "Gt past the last key must return none");
    }

    static private string Key(int i) => "player-" + i.ToString("D9");

    static private void AssertRange(bool condition, string message) {
        if (!condition) throw new InvalidOperationException("index is structurally wrong: " + message);
    }

    // Every result container is disposed.
    //
    // These methods read .Count off the container and drop it. That leaks the rented array back
    // to nobody, so the pool can never recycle it and every call allocates a fresh one - which
    // is how a width-1 range came to measure 280 B/op (ArrayPool<int>.Rent(64) = 256 B + header)
    // for a query that returns a single int. The production path through QuerySet disposes
    // correctly and does not pay this, so an undisposed benchmark measures a usage bug that the
    // generated code does not have.
    [Benchmark(Baseline = true)]
    public int Lookup_Int() => intIndex.GetOffset(probe).Unwrap();

    [Benchmark]
    public int Lookup_String() => strIndex.GetOffset(strProbe).Unwrap();

    [Benchmark]
    public int Lookup_Int_Random() => intIndex.GetOffset(randomProbes[nextProbe++ & (RandomProbeCount - 1)]).Unwrap();

    [Benchmark]
    public int Lookup_String_Random() => strIndex.GetOffset(randomStrProbes[nextProbe++ & (RandomProbeCount - 1)]).Unwrap();

    [Benchmark]
    public int Range_Width1() { using var r = intIndex.GetOffsetsRange(probe, probe); return r.Count; }

    [Benchmark]
    public int Range_Width100() { using var r = intIndex.GetOffsetsRange(probe, probe + 99); return r.Count; }

    [Benchmark]
    public int Range_Width10000() { using var r = intIndex.GetOffsetsRange(probe, probe + 9999); return r.Count; }

    // The generator's Iter() path: unbounded/unbounded, so the result length is known
    // before a single element is read.
    [Benchmark]
    public int Scan_All() { using var r = intIndex.GetOffsetsIter(); return r.Count; }

    [Benchmark]
    public int Scan_Except() { using var r = intIndex.GetOffsetsExcept(probe); return r.Count; }

    // Bulk load: dominated by splits, so it shows chunk allocation cost directly.
    [Benchmark]
    public int BulkLoad_1M() {
        var index = new BTreeIndex<int, DefaultComparer<int>>();
        for (var i = 0; i < BulkRows; i++) index.Insert(i, i);
        return index.Count;
    }

    [Benchmark]
    public int DeleteHalf_ThenInsertHalf() {
        var index = new BTreeIndex<int, DefaultComparer<int>>();
        for (var i = 0; i < Rows; i++) index.Insert(i, i);
        for (var i = 0; i < Rows; i += 2) index.Delete(i);
        for (var i = 0; i < Rows; i++) index.Insert(i, i);
        return index.Count;
    }
}