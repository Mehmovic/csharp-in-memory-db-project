using System.Runtime.CompilerServices;

namespace RhinoDB.Lib.Indexing.Test;

// Regression: a key that has left a BTree index must not stay reachable from it.
//
// The chunk arrays are pooled and every delete shifts the tail left by one, so the old
// implementation left the last key's reference sitting in the now-unused slot past Count,
// and returned retired chunks to ArrayPool without clearing them. For string (or any
// reference-carrying) keys that pinned every deleted key in memory for as long as the
// index - or the shared pool - lived. Nothing functional notices; only a GC can.
//
// Keys are built in [NoInlining] helpers so no stack slot in the test method keeps them
// alive, and each is a fresh (non-interned) string.
public class BTreeKeyRetentionTests {
    private const int Keys = 400;

    static private string KeyOf(int i) => "key-" + i.ToString("D5");

    [MethodImpl(MethodImplOptions.NoInlining)]
    static private WeakReference[] FillUnique(BTreeIndex<string, OrdinalStringComparer> index) {
        var refs = new WeakReference[Keys];
        for (var i = 0; i < Keys; i++) {
            var key = KeyOf(i);
            index.Insert(key, i);
            refs[i] = new WeakReference(key);
        }
        return refs;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static private WeakReference[] FillNonUnique(NonUniqueBTreeIndex<string, OrdinalStringComparer> index) {
        var refs = new WeakReference[Keys];
        for (var i = 0; i < Keys; i++) {
            var key = KeyOf(i);
            index.Insert(key, i);
            refs[i] = new WeakReference(key);
        }
        return refs;
    }

    static private void FullCollect() {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    static private int[] StillAlive(WeakReference[] refs, Func<int, bool> include)
        => Enumerable.Range(0, refs.Length).Where(i => include(i) && refs[i].IsAlive).ToArray();

    [Test]
    public void Unique_DeletingEveryKey_LeavesNoKeyReachable() {
        var index = new BTreeIndex<string, OrdinalStringComparer>(chunkSize: 16);
        var refs = FillUnique(index);

        for (var i = 0; i < Keys; i++) index.Delete(KeyOf(i));
        FullCollect();

        Assert.That(index.Count, Is.EqualTo(0));
        Assert.That(StillAlive(refs, _ => true), Is.Empty, "deleted keys are still referenced by the index");
        GC.KeepAlive(index);
    }

    [Test]
    public void Unique_DeletingEveryOtherKey_ReleasesExactlyTheDeletedOnes() {
        // Mid-chunk deletes (the tail-shift slot), chunk-last deletes (the max-key directory)
        // and merges all happen here, with survivors interleaved so nothing is freed wholesale.
        var index = new BTreeIndex<string, OrdinalStringComparer>(chunkSize: 16);
        var refs = FillUnique(index);

        for (var i = 1; i < Keys; i += 2) index.Delete(KeyOf(i));
        FullCollect();

        Assert.That(StillAlive(refs, i => i % 2 == 1), Is.Empty, "deleted keys are still referenced by the index");
        Assert.That(StillAlive(refs, i => i % 2 == 0), Has.Length.EqualTo(Keys / 2), "surviving keys must stay");
        GC.KeepAlive(index);
    }

    [Test]
    public void Unique_UpdateKey_ReleasesTheOldKey() {
        var index = new BTreeIndex<string, OrdinalStringComparer>(chunkSize: 16);
        var refs = FillUnique(index);

        for (var i = 0; i < Keys; i++) index.UpdateKey(KeyOf(i), "renamed-" + i.ToString("D5"), i);
        FullCollect();

        Assert.That(index.Count, Is.EqualTo(Keys));
        Assert.That(StillAlive(refs, _ => true), Is.Empty, "renamed-away keys are still referenced by the index");
        GC.KeepAlive(index);
    }

    [Test]
    public void Unique_BulkLoadFromOverAPopulatedIndex_ReleasesTheReplacedKeys() {
        var index = new BTreeIndex<string, OrdinalStringComparer>(chunkSize: 16);
        var refs = FillUnique(index);

        index.BulkLoadFrom(["fresh-a", "fresh-b"], [1, 2]);
        FullCollect();

        Assert.That(index.Count, Is.EqualTo(2));
        Assert.That(StillAlive(refs, _ => true), Is.Empty, "keys from before the reload are still referenced");
        GC.KeepAlive(index);
    }

    [Test]
    public void NonUnique_DeletingEveryOtherPair_ReleasesExactlyTheDeletedKeys() {
        var index = new NonUniqueBTreeIndex<string, OrdinalStringComparer>(chunkSize: 16);
        var refs = FillNonUnique(index);

        for (var i = 1; i < Keys; i += 2) index.Delete(KeyOf(i), i);
        FullCollect();

        Assert.That(StillAlive(refs, i => i % 2 == 1), Is.Empty, "deleted keys are still referenced by the index");
        Assert.That(StillAlive(refs, i => i % 2 == 0), Has.Length.EqualTo(Keys / 2), "surviving keys must stay");
        GC.KeepAlive(index);
    }
}
