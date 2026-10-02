using BenchmarkDotNet.Attributes;

using RhinoDB.Lib.Indexing;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

// RUNTIME insert and delete cost - the two operations a game engine actually leans on.
//
// Everything measured so far was build-time or read-time. This is the one that decides whether a
// tick loop can afford to mutate indexed columns at 60 Hz, so it is measured per operation at
// three scales, and it is measured PURELY.
//
// Purely is the hard part and it shapes the whole design. Insert and Delete are inverse, so timing
// them together only ever reports the sum. Instead each benchmark runs OperationsPerInvoke keys
// against a pre-built index and IterationSetup restores that index between iterations, untimed.
// So Insert_* times inserts only and Delete_* times deletes only, with no teardown mixed in.
//
// Two insert shapes, because they are not the same cost and only one of them is typical:
//
//   Ascending  - a new key lands after every existing key, so it appends to the last chunk.
//               Zero elements are moved. This is what a game generating sequential ids sees.
//   Scattered - a new key lands between two existing ones, so Array.Copy shifts everything to
//               its right within the chunk: up to chunkSize key copies AND the same number of
//               offset copies. This is the worst case and the one a random or externally-assigned
//               id produces.
//
// The gap between the two IS the answer. If they are close, insertion cost does not depend on
// where the key lands and the index is as good as it looks. If they diverge, a game cannot treat
// indexed writes as O(log n) and has to choose keys deliberately.
[MemoryDiagnoser]
public class IndexRuntimeBenchmarks {
    private const int Base = 1_000_000;

    // Batch size for one invocation. Large enough to amortise BDN's loop overhead past the
    // resolution of the numbers, small enough that the index only drifts by one batch within an
    // invocation - otherwise later invocations in the same iteration would measure a different
    // index size than earlier ones.
    private const int Batch = 2_000;

    private int[] ascending = null!;      // Batch..2*Batch, all above every key already present
    private int[] scattered = null!;      // Batch keys spread evenly through the populated range
    private int[] removals = null!;       // Batch keys guaranteed present, for the delete benches

    private HashIndex<int> hash = null!;
    private BTreeIndex<int> btree = null!;
    private NonUniqueBTreeIndex<int> nonUniqueBTree = null!;

    // Non-unique holds every key twice, so a delete has two offsets to find and remove.
    private int[] nonUniqueRemovals = null!;

    [GlobalSetup]
    public void Setup() {
        Build();
        AssertStructure();
    }

    [IterationSetup]
    public void Restore() => Build();

    // BulkLoad rather than N inserts: building the fixture 100k at a time with Insert would cost
    // more than the benchmark it is preparing for. This is the cold-storage load path, and it is
    // the same shape of work production would do.
    // The unique indexes are populated with EVEN keys only, leaving every odd key absent.
    //
    // That is not decoration. These are unique indexes, so inserting a key they already hold is
    // illegal - HashIndex threw ArgumentException on the first run of this benchmark, correctly.
    // With a densely packed 0..n-1 index there is therefore NO unused key that lands between two
    // existing ones, and "scattered insert" becomes untestable. Gaps give every odd key a home
    // strictly between two held keys, which is the only way to price the mid-chunk Array.Copy.
    //
    // Consequence: the index spans 0..2*Base, not 0..Base.
    private void Build() {
        var keys = new int[Base];
        var offsets = new int[Base];
        for (var i = 0; i < Base; i++) { keys[i] = i * 2; offsets[i] = i * 2; }

        // Hash gets Insert rather than a BulkLoad: a Dictionary has no chunk list to shift, so
        // there is no build-time cliff to optimise away, and adding a second bulk API for it
        // would be surface with no consumer.
        hash = new HashIndex<int>();
        for (var i = 0; i < Base; i++) hash.Insert(keys[i], offsets[i]);
        btree = BTreeIndex<int>.BulkLoad(keys, offsets);
        nonUniqueBTree = NonUniqueBTreeIndex<int>.BulkLoad(keys, offsets);
        for (var i = 0; i < Base; i++) nonUniqueBTree.Insert(keys[i], offsets[i]);

        // Above every populated key, so these append to the final chunk and move nothing.
        ascending = new int[Batch];
        for (var i = 0; i < Batch; i++) ascending[i] = Base * 2 + i;

        // Odd keys: each lands strictly between two held even keys, so every one of them shifts
        // the tail of its chunk.
        scattered = new int[Batch];
        var step = Math.Max(1, Base / Batch);
        for (var i = 0; i < Batch; i++) scattered[i] = (i * step) * 2 + 1;

        // Even keys that are present, spread across the whole span.
        removals = new int[Batch];
        for (var i = 0; i < Batch; i++) removals[i] = i * step * 2;
    }

    // Every index is proved to work before it is timed. A structurally broken index can time
    // beautifully and answer wrong, and insert/delete are exactly where that shows up as a
    // silently truncated or double-held index.
    private void AssertStructure() {
        for (var i = 0; i < Base; i += 9_973) {
            var key = i * 2;
            Assert(hash.GetOffset(key).Unwrap() == key, "hash must resolve every even key");
            Assert(btree.GetOffset(key).Unwrap() == key, "btree must resolve every even key");
        }
        // Odd keys must be absent - that is the gap the scattered-insert benchmarks rely on.
        Assert(hash.GetOffset(1).IsError(), "odd keys must not be present before the run");
        Assert(btree.GetOffset(1).IsError(), "odd keys must not be present before the run");
        using (var offsets = nonUniqueBTree.GetOffsets(1000)) {
            Assert(offsets.Count == 2, "non-unique must hold both offsets for a duplicated key");
        }
        Assert(btree.Count == Base, "btree count after build");
        Assert(nonUniqueBTree.Count == Base * 2, "non-unique count after build");
    }

    static private void Assert(bool condition, string message) {
        if (!condition) throw new InvalidOperationException("index is structurally wrong: " + message);
    }

    // --- unique BTree: the index a range-queryable column would use ---------------------

    [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
    public void BTree_Insert_Ascending() {
        foreach (var key in ascending) btree.Insert(key, key);
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public void BTree_Insert_Scattered() {
        foreach (var key in scattered) btree.Insert(key, key);
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public void BTree_Delete() {
        foreach (var key in removals) btree.Delete(key);
    }

    // --- unique Hash: the same three, to see what the ordering costs ----------------------

    [Benchmark(OperationsPerInvoke = Batch)]
    public void Hash_Insert_Ascending() {
        foreach (var key in ascending) hash.Insert(key, key);
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public void Hash_Insert_Scattered() {
        foreach (var key in scattered) hash.Insert(key, key);
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public void Hash_Delete() {
        foreach (var key in removals) hash.Delete(key);
    }

    // --- non-unique BTree: the secondary-index shape, where a delete removes one of many ----

    [Benchmark(OperationsPerInvoke = Batch)]
    public void NonUniqueBTree_Insert() {
        foreach (var key in scattered) nonUniqueBTree.Insert(key, key);
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public void NonUniqueBTree_Delete() {
        foreach (var key in removals) nonUniqueBTree.Delete(key, key);
    }

    // --- the cost that only appears on a split --------------------------------------------
    //
    // Every insert above targets an index that has spare room in its chunks, so none of them
    // split. A split is a different shape of work: allocate two arrays, copy half a chunk, and
    // insert into the chunk list. This forces exactly one split per operation by filling the
    // final chunk completely and then inserting the key that overflows it, which is the only way
    // to price that path in isolation.
    [Benchmark(OperationsPerInvoke = Batch)]
    public void BTree_Insert_ForcedSplit() {
        // A 16-entry index holds nothing spare, so nearly every insert splits.
        var tiny = new BTreeIndex<int>(16);
        for (var i = 0; i < 256; i++) tiny.Insert(i, i);
        for (var i = 0; i < Batch; i++) tiny.Insert(1000 + i, 1000 + i);
    }
}