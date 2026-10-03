using RhinoDB.Core;

namespace RhinoDB.Lib.Indexing.Test;

// Randomized differential tests: every BTree index is driven through thousands of mixed
// Insert/Delete/Update operations alongside a trivially-correct model (SortedDictionary /
// SortedSet), and every read surface is compared against the model at regular checkpoints.
//
// chunkSize 16 is deliberate: with ~2,000 live keys that is well over a hundred chunks, so
// splits, append-at-the-end splits, merges, and the chunk max-key directory are all exercised
// constantly. A bug in keeping that directory in sync with the chunks shows up here as a
// lookup or range that disagrees with the model, even when every hand-written case passes.
//
// The xorshift generator is seeded, so a failure always reproduces.
public class BTreeModelTests {
    private const int ChunkSize = 16;

    private struct XorShift(uint seed) {
        private uint state = seed | 1u;

        public int Next(int maxExclusive) {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (int)(state % (uint)maxExclusive);
        }
    }

    static private int[] Read(StackArrayPoolContainer<int> container) {
        using var c = container;
        return c.Buffer().ToArray();
    }

    // ---- unique ----

    static private void AssertUniqueMatchesModel(
        BTreeIndex<int, DefaultComparer<int>> index,
        SortedDictionary<int, int> model,
        ref XorShift rng,
        int keySpace,
        string at
    ) {
        Assert.That(index.Count, Is.EqualTo(model.Count), $"{at}: Count");
        Assert.That(Read(index.GetOffsetsIter()), Is.EqualTo(model.Values.ToArray()), $"{at}: full scan");

        for (var probe = 0; probe < 50; probe++) {
            var key = rng.Next(keySpace + 20) - 10;
            var result = index.GetOffset(key);
            if (model.TryGetValue(key, out var expected)) {
                Assert.That(result.IsOk(), Is.True, $"{at}: key {key} must resolve");
                Assert.That(result.Unwrap(), Is.EqualTo(expected), $"{at}: offset of {key}");
            } else {
                Assert.That(result.IsError(), Is.True, $"{at}: key {key} must not resolve");
            }
        }

        for (var probe = 0; probe < 20; probe++) {
            var a = rng.Next(keySpace + 20) - 10;
            var b = rng.Next(keySpace + 20) - 10;
            Assert.That(Read(index.GetOffsetsRange(a, b)), Is.EqualTo(model.Where(e => e.Key >= a && e.Key <= b).Select(e => e.Value).ToArray()), $"{at}: Range({a},{b})");
            Assert.That(Read(index.GetOffsetsGt(a)), Is.EqualTo(model.Where(e => e.Key > a).Select(e => e.Value).ToArray()), $"{at}: Gt({a})");
            Assert.That(Read(index.GetOffsetsGte(a)), Is.EqualTo(model.Where(e => e.Key >= a).Select(e => e.Value).ToArray()), $"{at}: Gte({a})");
            Assert.That(Read(index.GetOffsetsLt(a)), Is.EqualTo(model.Where(e => e.Key < a).Select(e => e.Value).ToArray()), $"{at}: Lt({a})");
            Assert.That(Read(index.GetOffsetsLte(a)), Is.EqualTo(model.Where(e => e.Key <= a).Select(e => e.Value).ToArray()), $"{at}: Lte({a})");
            Assert.That(Read(index.GetOffsetsExcept(a)), Is.EqualTo(model.Where(e => e.Key != a).Select(e => e.Value).ToArray()), $"{at}: Except({a})");
        }
    }

    [TestCase(1u)]
    [TestCase(42u)]
    [TestCase(9_001u)]
    public void Unique_RandomOperations_AlwaysAgreeWithASortedDictionary(uint seed) {
        const int keySpace = 3_000;
        var rng = new XorShift(seed);
        var index = new BTreeIndex<int, DefaultComparer<int>>(ChunkSize);
        var model = new SortedDictionary<int, int>();
        var nextOffset = 0;

        for (var op = 1; op <= 20_000; op++) {
            var key = rng.Next(keySpace);
            switch (rng.Next(10)) {
                case < 5:
                    if (model.ContainsKey(key)) break;
                    index.Insert(key, nextOffset);
                    model[key] = nextOffset++;
                    break;
                case < 8:
                    index.Delete(key);
                    model.Remove(key);
                    break;
                case 8:
                    if (!model.ContainsKey(key)) break;
                    index.UpdateOffset(key, nextOffset);
                    model[key] = nextOffset++;
                    break;
                default:
                    var newKey = rng.Next(keySpace);
                    if (!model.ContainsKey(key) || model.ContainsKey(newKey)) break;
                    index.UpdateKey(key, newKey, nextOffset);
                    model.Remove(key);
                    model[newKey] = nextOffset++;
                    break;
            }

            if (op % 1_000 == 0) AssertUniqueMatchesModel(index, model, ref rng, keySpace, $"seed {seed}, op {op}");
        }
    }

    [Test]
    public void Unique_AscendingLoadThenRandomChurn_AgreesWithTheModel() {
        // Ascending inserts take the append-at-the-end split (the new chunk holds only the new
        // key); the churn afterwards then has to split and merge those packed chunks normally.
        var rng = new XorShift(7);
        var index = new BTreeIndex<int, DefaultComparer<int>>(ChunkSize);
        var model = new SortedDictionary<int, int>();
        for (var key = 0; key < 4_000; key += 2) {
            index.Insert(key, key);
            model[key] = key;
        }
        AssertUniqueMatchesModel(index, model, ref rng, 4_000, "after ascending load");

        for (var op = 1; op <= 10_000; op++) {
            var key = rng.Next(4_000);
            if (rng.Next(2) == 0) {
                if (model.ContainsKey(key)) continue;
                index.Insert(key, key);
                model[key] = key;
            } else {
                index.Delete(key);
                model.Remove(key);
            }
            if (op % 1_000 == 0) AssertUniqueMatchesModel(index, model, ref rng, 4_000, $"churn op {op}");
        }
    }

    [Test]
    public void Unique_DrainedToEmptyAndRefilled_AgreesWithTheModel() {
        var rng = new XorShift(3);
        var index = new BTreeIndex<int, DefaultComparer<int>>(ChunkSize);
        var model = new SortedDictionary<int, int>();

        for (var round = 0; round < 3; round++) {
            for (var i = 0; i < 1_000; i++) {
                var key = (i * 677) % 1_009;
                index.Insert(key, key + round);
                model[key] = key + round;
            }
            AssertUniqueMatchesModel(index, model, ref rng, 1_009, $"round {round} filled");

            foreach (var key in model.Keys.ToArray()) {
                index.Delete(key);
                model.Remove(key);
            }
            AssertUniqueMatchesModel(index, model, ref rng, 1_009, $"round {round} drained");
            Assert.That(index.ChunkCount, Is.EqualTo(1), "a drained index keeps exactly one empty chunk");
        }
    }

    // ---- non-unique ----

    static private void AssertNonUniqueMatchesModel(
        NonUniqueBTreeIndex<int, DefaultComparer<int>> index,
        SortedSet<(int Key, int Offset)> model,
        ref XorShift rng,
        int keySpace,
        string at
    ) {
        Assert.That(index.Count, Is.EqualTo(model.Count), $"{at}: Count");
        Assert.That(Read(index.GetOffsetsIter()), Is.EqualTo(model.Select(e => e.Offset).ToArray()),
            $"{at}: full scan must be ordered by (key, offset)");

        for (var probe = 0; probe < 30; probe++) {
            var key = rng.Next(keySpace + 4) - 2;
            Assert.That(Read(index.GetOffsets(key)), Is.EqualTo(model.Where(e => e.Key == key).Select(e => e.Offset).ToArray()),
                $"{at}: GetOffsets({key})");
        }

        for (var probe = 0; probe < 20; probe++) {
            var a = rng.Next(keySpace + 4) - 2;
            var b = rng.Next(keySpace + 4) - 2;
            Assert.That(Read(index.GetOffsetsRange(a, b)), Is.EqualTo(model.Where(e => e.Key >= a && e.Key <= b).Select(e => e.Offset).ToArray()), $"{at}: Range({a},{b})");
            Assert.That(Read(index.GetOffsetsGt(a)), Is.EqualTo(model.Where(e => e.Key > a).Select(e => e.Offset).ToArray()), $"{at}: Gt({a})");
            Assert.That(Read(index.GetOffsetsLt(a)), Is.EqualTo(model.Where(e => e.Key < a).Select(e => e.Offset).ToArray()), $"{at}: Lt({a})");
            Assert.That(Read(index.GetOffsetsExcept(a)), Is.EqualTo(model.Where(e => e.Key != a).Select(e => e.Offset).ToArray()), $"{at}: Except({a})");
        }
    }

    [TestCase(1u, 40)]
    [TestCase(77u, 400)]
    [TestCase(123u, 5)]
    public void NonUnique_RandomOperations_AlwaysAgreeWithASortedSetOfPairs(uint seed, int keySpace) {
        // keySpace 5 means duplicate runs hundreds of entries long, spanning many chunks; 400
        // means short runs that straddle chunk boundaries only occasionally.
        var rng = new XorShift(seed);
        var index = new NonUniqueBTreeIndex<int, DefaultComparer<int>>(ChunkSize);
        var model = new SortedSet<(int Key, int Offset)>();
        var live = new List<(int Key, int Offset)>();
        var nextOffset = 0;

        for (var op = 1; op <= 15_000; op++) {
            if (live.Count == 0 || rng.Next(10) < 6) {
                var key = rng.Next(keySpace);
                var offset = (int)((nextOffset++ * 7_919L) % 1_000_003); // unique, but not in insertion order
                index.Insert(key, offset);
                model.Add((key, offset));
                live.Add((key, offset));
            } else {
                var victim = rng.Next(live.Count);
                var (key, offset) = live[victim];
                live[victim] = live[^1];
                live.RemoveAt(live.Count - 1);
                index.Delete(key, offset);
                model.Remove((key, offset));
            }

            if (op % 1_000 == 0) AssertNonUniqueMatchesModel(index, model, ref rng, keySpace, $"seed {seed}, op {op}");
        }
    }

    [Test]
    public void NonUnique_DeletingAPairThatIsNotThere_ChangesNothing() {
        var rng = new XorShift(5);
        var index = new NonUniqueBTreeIndex<int, DefaultComparer<int>>(ChunkSize);
        var model = new SortedSet<(int Key, int Offset)>();
        for (var i = 0; i < 300; i++) {
            index.Insert(i % 7, i);
            model.Add((i % 7, i));
        }

        index.Delete(3, 4);      // key 3 exists, but offset 4 belongs to key 4
        index.Delete(99, 1);     // key above everything
        index.Delete(-1, 0);     // key below everything
        index.Delete(6, 10_000); // offset above every offset of the last key

        AssertNonUniqueMatchesModel(index, model, ref rng, 7, "after no-op deletes");
    }

    // ---- string keys through the ordinal comparer ----

    [Test]
    public void OrdinalString_RandomOperations_AgreeWithAnOrdinalSortedDictionary() {
        var rng = new XorShift(11);
        var index = new BTreeIndex<string, OrdinalStringComparer>(ChunkSize);
        var model = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // Mixed case on purpose: ordinal puts every upper-case letter before every lower-case
        // one, culture ordering interleaves them - so a wrong comparer reorders the scan.
        string KeyOf(int n) => $"{(char)((n % 2 == 0 ? 'A' : 'a') + n % 26)}-{n:D4}";

        for (var op = 1; op <= 8_000; op++) {
            var key = KeyOf(rng.Next(1_500));
            if (rng.Next(3) < 2) {
                if (model.ContainsKey(key)) continue;
                index.Insert(key, op);
                model[key] = op;
            } else {
                index.Delete(key);
                model.Remove(key);
            }

            if (op % 1_000 != 0) continue;
            Assert.That(index.Count, Is.EqualTo(model.Count));
            Assert.That(Read(index.GetOffsetsIter()), Is.EqualTo(model.Values.ToArray()), $"op {op}: ordinal scan order");
            var from = KeyOf(rng.Next(1_500));
            var to = KeyOf(rng.Next(1_500));
            Assert.That(Read(index.GetOffsetsRange(from, to)),
                Is.EqualTo(model.Where(e => string.CompareOrdinal(e.Key, from) >= 0 && string.CompareOrdinal(e.Key, to) <= 0).Select(e => e.Value).ToArray()),
                $"op {op}: Range({from},{to})");
        }
    }
}
