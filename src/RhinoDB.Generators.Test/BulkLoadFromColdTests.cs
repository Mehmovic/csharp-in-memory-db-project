using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// BulkLoadFromCold is the "first run" path: cold storage is loaded into memory at startup.
// It used to do Insert on every index, row by row.
//
// The existing LoaderTests do NOT cover that: their schema uses the default [PrimaryKey], which
// is IndexKind.Hash, and declares no secondary indexes. So the whole BTree bulk path - the
// primary BTree index and every BTree secondary index - was unexercised.
//
// The schema below is chosen to hit all of it at once: a BTree primary key, a UNIQUE BTree
// secondary, a NON-UNIQUE BTree secondary, and a composite BTree index whose key is a
// ValueTuple containing a string (the shape that needs a comparer). Rows are inserted in
// DESCENDING primary-key order so that a per-row load would split chunks constantly - the exact
// cliff this path exists to remove - which also means a wrong result cannot hide behind an
// accidentally-friendly order.
public class BulkLoadFromColdTests {
    private const int Rows = 3_000;

    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class BulkDb : DbContext<BulkDbTransaction> { }

        [Table(TableKind.Persistent, typeof(BulkDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Item(
            [PrimaryKey(IndexKind.BTree)] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [Index(IndexKind.BTree, Uniqueness.Unique, Accessor = "ByScore")] [property: MemoryPackOrder(1)] [property: Key(1)] int Score,
            [Index(IndexKind.BTree, Accessor = "ByLabel")] [property: MemoryPackOrder(2)] [property: Key(2)] string Label,
            [Index(IndexKind.BTree, Accessor = "ByClubAndRegion", Order = 0)] [property: MemoryPackOrder(3)] [property: Key(3)] int ClubId,
            [Index(IndexKind.BTree, Accessor = "ByClubAndRegion", Order = 1)] [property: MemoryPackOrder(4)] [property: Key(4)] string Region);

        public static class TestHelpers {
            public static bool PrimaryFinds(BulkDbItemOps ops, int id) => ops.Primary.Find(id).HasRow();
            public static bool UniqueSecondaryFinds(BulkDbItemOps ops, int score) => ops.Idx.ByScore.Find(score).HasRow();
            public static int NonUniqueSecondaryCount(BulkDbItemOps ops, string label) => ops.Idx.ByLabel.Find(label).Count;
            public static int CompositeCount(BulkDbItemOps ops, int clubId, string region) => ops.Idx.ByClubAndRegion.Find(clubId, region).Count;
            public static int RowCount(BulkDbItemOps ops) => ops.Iter().Count;
            public static int TotalCount(BulkDbItemOps ops) => ops.Primary.Iter().Count;
        }
        """;

    static private object NewItem(System.Reflection.Assembly asm, int id, int score, string label, int clubId, string region) {
        var t = asm.GetType("TestNs.Item")!;
        return Activator.CreateInstance(t, id, score, label, clubId, region)!;
    }

    // Label cycles through 10 values and clubId through 30, and region is i % 3 - which is fully
    // determined by clubId because 30 is a multiple of 3. So every label holds Rows/10 rows and
    // every (clubId, region) pair holds Rows/30 rows. A load that dropped or duplicated a row
    // would break those exact counts.
    static private int ExpectedPerLabel => Rows / 10;
    static private int ExpectedPerClubRegion => Rows / 30;

    [Test]
    public async Task BulkLoadFromCold_FillsEveryBTreeIndexOfEveryRow() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-bulkload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            System.Reflection.Assembly asm;
            Type dbType, txType;
            using (var cold = ColdStore.Open(dir).Unwrap()) {
                (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
                dbType = asm.GetType("TestNs.BulkDb")!;
                txType = asm.GetType("TestNs.BulkDbTransaction")!;
                var db = Activator.CreateInstance(dbType, cold)!;

                var insert = await (Task<Result>)GeneratorTestHost.RunTransactional(
                    db, txType, (ctx, tx) => {
                        dynamic dtx = tx;
                        // Descending, so a per-row load would split on nearly every insert.
                        for (var i = Rows - 1; i >= 0; i--)
                            dtx.Item.Insert((dynamic)NewItem(asm, i, i, $"label-{i % 10}", i % 30, $"region-{i % 3}"));
                        return Result.Ok();
                    }, PropagationMode.Confirmed);
                Assert.That(insert.IsOk(), Is.True);
            }

            // Reopen cold: a brand new in-memory database with nothing loaded.
            using var reopenedCold = ColdStore.Open(dir).Unwrap();
            var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
            reopenedCold.CompleteRecovery();

            var createLoaderTx = dbType.GetMethod("CreateLoaderTransaction",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            dynamic tx = createLoaderTx.Invoke(reopenedDb, null)!;
            var itemOps = tx.Item;
            Assert.That((int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "TotalCount", (object)itemOps)!,
                Is.EqualTo(0), "the reopened database starts empty - otherwise this proves nothing");

            var bulkLoad = itemOps.GetType().GetMethod("BulkLoadFromCold",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            bulkLoad.Invoke((object)itemOps, null);

            int Count(string helper, params object[] args) =>
                (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", helper, [.. new object[] { itemOps }.Concat(args)])!;
            bool Flag(string helper, params object[] args) =>
                (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", helper, [.. new object[] { itemOps }.Concat(args)])!;

            Assert.That(Count("TotalCount"), Is.EqualTo(Rows), "every row must be in the primary index");
            Assert.That(Count("RowCount"), Is.EqualTo(Rows), "every row must be reachable through Iter()");

            // Spot-check across the whole span rather than just the ends: an index that only got
            // part of the data still answers correctly for the keys it happens to hold.
            for (var i = 0; i < Rows; i += 37)
                Assert.That(Flag("PrimaryFinds", i), Is.True, $"BTree primary index must hold key {i}");

            for (var i = 0; i < Rows; i += 41)
                Assert.That(Flag("UniqueSecondaryFinds", i), Is.True, $"unique BTree secondary must hold score {i}");

            for (var n = 0; n < 10; n++)
                Assert.That(Count("NonUniqueSecondaryCount", $"label-{n}"), Is.EqualTo(ExpectedPerLabel),
                    $"non-unique BTree secondary must hold every row for label-{n}");

            for (var i = 0; i < Rows; i += 53)
                Assert.That(Count("CompositeCount", i % 30, $"region-{i % 3}"), Is.EqualTo(ExpectedPerClubRegion),
                    $"composite (int, string) BTree index must hold the row for club {i % 30}, region {i % 3}");

            Assert.That(Flag("PrimaryFinds", Rows + 1), Is.False, "a key that was never loaded must stay absent");
        } finally {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
