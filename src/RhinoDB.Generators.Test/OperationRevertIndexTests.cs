using System.Reflection;

using RhinoDB.Core;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Storage;

namespace RhinoDB.Generators.Test;

// Operation-level revert across every index kind and both table kinds. The original
// OperationRevertTests file covers exactly one shape - a Hash primary key with a Hash
// unique secondary and a Hash non-unique secondary, on Instant tables - which leaves
// the BTree indexes, the composite index, the persistent-table path, and the
// interesting half of the revert untested. Revert is where index maintenance is
// hardest to get right: it has to undo Apply's index work exactly, not merely restore
// a value, and the cases differ:
//   - an update that does NOT move an index entry (nothing to undo)
//   - an update that DOES move one (the applied key must come back out)
//   - a delete (the entries must go back in) and an insert (they must come back out)
// A revert that only restored row values would pass the first case and fail the rest.
public class OperationRevertIndexTests {
    private const string Source = """
        using System;
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class RevDb : DbContext<RevDbTransaction> { }

        // Hash primary key, unique Hash secondary, non-unique Hash secondary.
        [Table<RevDb>(TableKind.Instant)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Item(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [Index(IndexKind.Hash, Uniqueness.Unique)] [property: MemoryPackOrder(1)] [property: Key(1)] int Sku,
            [Index(IndexKind.Hash, Uniqueness.NonUnique)] [property: MemoryPackOrder(2)] [property: Key(2)] int Grp,
            [property: MemoryPackOrder(3)] [property: Key(3)] int Qty);

        // BTree primary key, unique BTree secondary, non-unique BTree secondary - the
        // chunk-based index, where an entry's slot can move on a split or a swap-remove.
        [Table<RevDb>(TableKind.Instant, RingBufferCapacity = 32)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Rank(
            [PrimaryKey(IndexKind.BTree)] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [Index(IndexKind.BTree, Uniqueness.Unique)] [property: MemoryPackOrder(1)] [property: Key(1)] int Code,
            [Index(IndexKind.BTree, Uniqueness.NonUnique)] [property: MemoryPackOrder(2)] [property: Key(2)] int Tier,
            [property: MemoryPackOrder(3)] [property: Key(3)] int Score);

        // A composite unique Hash index over two columns.
        [Table<RevDb>(TableKind.Instant)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Pair(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "ByAB", Order = 0)] [property: MemoryPackOrder(1)] [property: Key(1)] int A,
            [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "ByAB", Order = 1)] [property: MemoryPackOrder(2)] [property: Key(2)] int B,
            [property: MemoryPackOrder(3)] [property: Key(3)] int Note);

        // QuerySet/QuerySingle are ref structs and cannot cross a dynamic call boundary
        // (see GeneratorTestHost.InvokeHelper), so every read goes through one of these
        // statically-typed helpers, whose signatures stay reflection-safe.
        public static class Probe {
            public static int ItemCount(RevDbItemOps t) { using var q = t.Iter(); return q.Get().Unwrap().Length; }
            public static bool ItemExists(RevDbItemOps t, int id) => t.Primary.Find(id).HasRow();
            public static int ItemQty(RevDbItemOps t, int id) => t.Primary.Find(id).Get().Unwrap().Qty;
            public static bool ItemBySku(RevDbItemOps t, int sku) => t.Idx.Sku.Find(sku).HasRow();
            public static int ItemByGrp(RevDbItemOps t, int grp) { using var q = t.Idx.Grp.Find(grp); return q.Count; }

            public static int RankCount(RevDbRankOps t) { using var q = t.Iter(); return q.Get().Unwrap().Length; }
            public static bool RankExists(RevDbRankOps t, int id) => t.Primary.Find(id).HasRow();
            public static int RankScore(RevDbRankOps t, int id) => t.Primary.Find(id).Get().Unwrap().Score;
            public static bool RankByCode(RevDbRankOps t, int code) => t.Idx.Code.Find(code).HasRow();
            public static int RankByTier(RevDbRankOps t, int tier) { using var q = t.Idx.Tier.Find(tier); return q.Count; }

            // Reads the ring directly so the test can prove a reverted operation left no
            // trace for a client to sync.
            public static int RankPendingOrphans(RevDbRankOps t) => t.PendingStorageOrphanCount;
            public static int RankRingCountSince(RevDbRankOps t, ulong lsn) {
                using var e = t.TryGetChangesSince(lsn).Unwrap();
                return e.Count;
            }
            public static int PairCount(RevDbPairOps t) { using var q = t.Iter(); return q.Get().Unwrap().Length; }
            public static bool PairByAB(RevDbPairOps t, int a, int b) => t.Idx.ByAB.Find(a, b).HasRow();
        }
        """;

    static private (object Db, Type TxType, System.Reflection.Assembly Asm) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        return (Activator.CreateInstance(asm.GetType("TestNs.RevDb")!)!, asm.GetType("TestNs.RevDbTransaction")!, asm);
    }

    static private object NewItem(System.Reflection.Assembly asm, int id, int sku, int grp, int qty)
        => Activator.CreateInstance(asm.GetType("TestNs.Item")!, id, sku, grp, qty)!;

    static private object NewRank(System.Reflection.Assembly asm, int id, int code, int tier, int score)
        => Activator.CreateInstance(asm.GetType("TestNs.Rank")!, id, code, tier, score)!;

    static private object NewPair(System.Reflection.Assembly asm, int id, int a, int b, int note)
        => Activator.CreateInstance(asm.GetType("TestNs.Pair")!, id, a, b, note)!;

    static private Result Run(object db, Type txType, Func<object, Result> body)
        => (Result)((Task<Result>)GeneratorTestHost.RunTransactional(db, txType, (ctx, tx) => body(tx), PropagationMode.Optimistic)).Result;

    // The transaction exposes each table accessor as a public readonly FIELD
    // (TableGenerator: "public readonly {Db}{Accessor}Ops {Accessor};"), so GetProperty
    // returns null here - GetField is the correct accessor.
    // Persistent-kind tables are still a flat field/property on tx; Instant-kind tables moved
    // under a nested tx.Instant accessor (TableGenerator.EmitDatabase) - fall back to resolving
    // through that nested accessor when the name is not found directly on tx itself.
    static private object T(object tx, string accessor) {
        var type = tx.GetType();
        var field = type.GetField(accessor);
        if (field is not null) return field.GetValue(tx)!;
        var prop = type.GetProperty(accessor);
        if (prop is not null) return prop.GetValue(tx)!;

        var instant = type.GetProperty("Instant")!.GetValue(tx)!;
        var instantType = instant.GetType();
        var instantField = instantType.GetField(accessor);
        return instantField is not null ? instantField.GetValue(instant)! : instantType.GetProperty(accessor)!.GetValue(instant)!;
    }

    // Arms a one-shot fault on one table's Apply at the given change ordinal. One-shot
    // because the ops object is a per-transaction singleton: a fault left armed would
    // fire again on the next operation and make the asserts below meaningless.
    static private void ArmFault(object tx, string accessor, int throwAt) {
        var ops = T(tx, accessor);
        var prop = ops.GetType().GetProperty("TestOnlyApplyFault")!;
        prop.SetValue(ops, (Action<int>)(i => {
            if (i != throwAt) return;
            prop.SetValue(ops, null);
            throw new InvalidOperationException($"injected fault at change {i}");
        }));
    }

    static private void ArmRevertFault(object tx, string accessor) {
        var ops = T(tx, accessor);
        ops.GetType().GetProperty("TestOnlyRevertFault")!.SetValue(ops, (Action)(() => {
            throw new InvalidOperationException("injected fault during revert");
        }));
    }

    static private object? Probe(System.Reflection.Assembly asm, string method, object tx, string accessor, params object?[] args)
        => GeneratorTestHost.InvokeHelper(asm, "TestNs.Probe", method, [T(tx, accessor), .. args]);

    // Reads a table straight off the database, between operations - also the only way to look at a poisoned one.
    // Never assert inside a Run body: the operation catches whatever the body throws (a failed Assert, a missing
    // probe) and turns it into an error Result, so the test would pass without having checked anything.
    static private object OpsOf(object db, string accessor) =>
        db.GetType().GetField(char.ToLowerInvariant(accessor[0]) + accessor[1..] + "Ops", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(db)!;

    static private object? ProbeOps(System.Reflection.Assembly asm, string method, object db, string accessor, params object?[] args)
        => GeneratorTestHost.InvokeHelper(asm, "TestNs.Probe", method, [OpsOf(db, accessor), .. args]);

    static private int PendingOrphans(object db, string accessor) => ((dynamic)OpsOf(db, accessor)).PendingStorageOrphanCount;

    static private int StorageSlots(object db, string accessor) {
        var ops = OpsOf(db, accessor);
        var storage = ops.GetType().GetField("storage", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ops)!;
        return (int)storage.GetType().GetProperty("Count")!.GetValue(storage)!;
    }

    // Cleanup's init accessor is private to DbContext<TTx>, so only the declaring type's PropertyInfo can reach it.
    // An operation hands back its result before its trailing sweep runs (PooledOperation.Run), so a read straight
    // after Run races that sweep. A no-op operation queues behind it on the loop - once it returns, the table is still.
    static private void Settle(object db, Type txType) => Run(db, txType, _ => Result.Ok());

    static private void SetCleanup(object db, CleanupCollector cleanup) =>
        db.GetType().GetProperty("Cleanup")!.DeclaringType!.GetProperty("Cleanup")!.SetValue(db, cleanup);
    // ---- Hash indexes ----

    [Test]
    public void RevertOfAnAppliedInsert_LiftsItsIndexEntriesBackOut() {
        var (db, txType, asm) = NewDb();

        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 1, 100, 7, 5)); return Result.Ok(); });

        // Change 0 (the insert) applies; the fault stops change 1 before it starts, so
        // exactly one row was appended and its three index entries were written.
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 2, 300, 7, 1));
            ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 3, 400, 7, 1));
            ArmFault(tx, "Item", 1);
            return Result.Ok();
        });

        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "ItemCount", tx, "Item")!, Is.EqualTo(1), "the appended row is gone");
            Assert.That((bool)Probe(asm, "ItemExists", tx, "Item", 2)!, Is.False, "its primary-index entry is gone");
            Assert.That((bool)Probe(asm, "ItemBySku", tx, "Item", 300)!, Is.False, "its unique secondary entry is gone");
            Assert.That((int)Probe(asm, "ItemByGrp", tx, "Item", 7)!, Is.EqualTo(1), "its non-unique secondary entry is gone");
            return Result.Ok();
        });
    }

    [Test]
    public void RevertOfAnUpdateThatLeavesIndexedFieldsUnchanged_LeavesEveryEntryAlone() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 1, 100, 7, 5)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 2, 300, 7, 1)); return Result.Ok(); });

        // Only Qty changes - neither Sku nor Grp - so Apply touches no index entry and
        // the revert must not invent or drop one either.
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Item.Update(1, (dynamic)NewItem(asm, 1, 100, 7, 99));
            ((dynamic)tx).Instant.Item.Update(2, (dynamic)NewItem(asm, 2, 300, 7, 99));
            ArmFault(tx, "Item", 1);
            return Result.Ok();
        });

        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "ItemQty", tx, "Item", 1)!, Is.EqualTo(5), "the value is back");
            Assert.That((bool)Probe(asm, "ItemBySku", tx, "Item", 100)!, Is.True);
            Assert.That((int)Probe(asm, "ItemByGrp", tx, "Item", 7)!, Is.EqualTo(2), "the count is untouched");
            return Result.Ok();
        });
    }

    [Test]
    public void RevertOfAnUpdateThatMovedAUniqueEntry_DropsTheAppliedKey() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 1, 100, 7, 5)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 2, 300, 8, 1)); return Result.Ok(); });

        // Change 0 moves Sku 100 -> 200; the fault stops change 1.
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Item.Update(1, (dynamic)NewItem(asm, 1, 200, 7, 5));
            ((dynamic)tx).Instant.Item.Update(2, (dynamic)NewItem(asm, 2, 301, 8, 1));
            ArmFault(tx, "Item", 1);
            return Result.Ok();
        });

        Run(db, txType, tx => {
            Assert.That((bool)Probe(asm, "ItemBySku", tx, "Item", 200)!, Is.False, "the key Apply installed must be lifted back out");
            Assert.That((bool)Probe(asm, "ItemBySku", tx, "Item", 100)!, Is.True, "the original key must resolve again");
            return Result.Ok();
        });
    }

    [Test]
    public void RevertOfAnUpdateThatMovedANonUniqueEntry_RestoresBothCounts() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 1, 100, 7, 5)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 2, 300, 7, 1)); return Result.Ok(); });

        // Change 0 moves Grp 7 -> 9, leaving only row 2 behind in the 7 bucket.
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Item.Update(1, (dynamic)NewItem(asm, 1, 100, 9, 5));
            ((dynamic)tx).Instant.Item.Update(2, (dynamic)NewItem(asm, 2, 300, 7, 9));
            ArmFault(tx, "Item", 1);
            return Result.Ok();
        });

        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "ItemByGrp", tx, "Item", 9)!, Is.EqualTo(0), "the applied bucket entry must be lifted back out");
            Assert.That((int)Probe(asm, "ItemByGrp", tx, "Item", 7)!, Is.EqualTo(2), "both rows are back in the 7 bucket");
            return Result.Ok();
        });
    }

    [Test]
    public void RevertOfADelete_PutsTheHashIndexEntriesBack() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 1, 100, 7, 5)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 2, 300, 7, 1)); return Result.Ok(); });

        // Change 0 (the delete) applies - removing the row's primary and both secondary
        // entries - then the fault stops change 1.
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Item.Delete(1);
            ((dynamic)tx).Instant.Item.Delete(2);
            ArmFault(tx, "Item", 1);
            return Result.Ok();
        });

        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "ItemCount", tx, "Item")!, Is.EqualTo(2), "the row is back");
            Assert.That((bool)Probe(asm, "ItemExists", tx, "Item", 1)!, Is.True, "its primary entry is back");
            Assert.That((bool)Probe(asm, "ItemBySku", tx, "Item", 100)!, Is.True, "its unique secondary entry is back");
            Assert.That((int)Probe(asm, "ItemByGrp", tx, "Item", 7)!, Is.EqualTo(2), "its non-unique secondary entry is back");
            return Result.Ok();
        });
    }


    // ---- BTree indexes: chunk-based, so an entry's slot can move on a split ----
    [Test]
    public void RevertOfAnInsertIntoBTreeIndexes_RemovesEveryEntry() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 1, 10, 1, 100)); return Result.Ok(); });
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 2, 20, 2, 200));
            ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 3, 30, 3, 300));
            ArmFault(tx, "Rank", 1);
            return Result.Ok();
        });
        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "RankCount", tx, "Rank")!, Is.EqualTo(1), "the whole insert is truncated away");
            Assert.That((bool)Probe(asm, "RankExists", tx, "Rank", 1)!, Is.True, "the row that predates the operation is untouched");
            Assert.That((bool)Probe(asm, "RankByCode", tx, "Rank", 20)!, Is.False, "no BTree unique entry survives");
            Assert.That((bool)Probe(asm, "RankByCode", tx, "Rank", 30)!, Is.False, "no BTree unique entry survives");
            Assert.That((int)Probe(asm, "RankByTier", tx, "Rank", 2)!, Is.EqualTo(0));
            Assert.That((int)Probe(asm, "RankByTier", tx, "Rank", 3)!, Is.EqualTo(0));
            return Result.Ok();
        });
    }
    [Test]
    public void RevertOfAnUpdateThatMovedABTreeEntry_RestoresTheOriginalKey() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 1, 10, 1, 100)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 2, 20, 2, 200)); return Result.Ok(); });
        // Change 0 moves Code 10 -> 99 and Tier 1 -> 9 on the BTree indexes.
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Rank.Update(1, (dynamic)NewRank(asm, 1, 99, 9, 555));
            ((dynamic)tx).Instant.Rank.Update(2, (dynamic)NewRank(asm, 2, 21, 2, 201));
            ArmFault(tx, "Rank", 1);
            return Result.Ok();
        });
        Run(db, txType, tx => {
            Assert.That((bool)Probe(asm, "RankByCode", tx, "Rank", 99)!, Is.False, "the key Apply installed must be lifted back out");
            Assert.That((bool)Probe(asm, "RankByCode", tx, "Rank", 10)!, Is.True, "the original BTree unique key resolves again");
            Assert.That((int)Probe(asm, "RankByTier", tx, "Rank", 9)!, Is.EqualTo(0));
            Assert.That((int)Probe(asm, "RankByTier", tx, "Rank", 1)!, Is.EqualTo(1));
            Assert.That((int)Probe(asm, "RankScore", tx, "Rank", 1)!, Is.EqualTo(100), "the row value itself is restored");
            return Result.Ok();
        });
    }
    [Test]
    public void RevertOfADelete_PutsTheBTreeIndexEntriesBack() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 1, 10, 1, 100)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 2, 20, 1, 200)); return Result.Ok(); });
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Rank.Delete(1);
            ((dynamic)tx).Instant.Rank.Delete(2);
            ArmFault(tx, "Rank", 1);
            return Result.Ok();
        });
        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "RankCount", tx, "Rank")!, Is.EqualTo(2), "the row is back");
            Assert.That((bool)Probe(asm, "RankExists", tx, "Rank", 1)!, Is.True);
            Assert.That((bool)Probe(asm, "RankByCode", tx, "Rank", 10)!, Is.True, "its BTree unique entry is back");
            Assert.That((int)Probe(asm, "RankByTier", tx, "Rank", 1)!, Is.EqualTo(2), "its BTree non-unique entry is back");
            return Result.Ok();
        });
    }
    // ---- composite unique index ----
    [Test]
    public void RevertOfAnUpdateThatMovedACompositeEntry_RestoresTheWholeKey() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 1, 5, 6, 10)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 2, 7, 8, 20)); return Result.Ok(); });
        // Change 0 moves the composite key (5,6) -> (50,60).
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Pair.Update(1, (dynamic)NewPair(asm, 1, 50, 60, 99));
            ((dynamic)tx).Instant.Pair.Update(2, (dynamic)NewPair(asm, 2, 7, 9, 21));
            ArmFault(tx, "Pair", 1);
            return Result.Ok();
        });
        Run(db, txType, tx => {
            Assert.That((bool)Probe(asm, "PairByAB", tx, "Pair", 50, 60)!, Is.False, "the composite key Apply installed must be lifted back out");
            Assert.That((bool)Probe(asm, "PairByAB", tx, "Pair", 5, 6)!, Is.True, "the original composite key resolves again");
            return Result.Ok();
        });
    }
    [Test]
    public void RevertOfADelete_PutsTheCompositeEntryBack() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 1, 5, 6, 10)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 2, 7, 8, 20)); return Result.Ok(); });
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Pair.Delete(1);
            ((dynamic)tx).Instant.Pair.Delete(2);
            ArmFault(tx, "Pair", 1);
            return Result.Ok();
        });
        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "PairCount", tx, "Pair")!, Is.EqualTo(2));
            Assert.That((bool)Probe(asm, "PairByAB", tx, "Pair", 5, 6)!, Is.True, "the composite entry is back");
            return Result.Ok();
        });
    }
    // ---- revert must not disturb a table the operation never wrote ----
    [Test]
    public void RevertLeavesAnUntouchedTableAlone() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 1, 10, 1, 100)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 1, 100, 7, 5)); return Result.Ok(); });
        // Only Item is written; Rank must come through the revert untouched.
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 2, 300, 8, 1));
            ((dynamic)tx).Instant.Item.Insert((dynamic)NewItem(asm, 3, 400, 8, 2));
            ArmFault(tx, "Item", 1);
            return Result.Ok();
        });
        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "RankCount", tx, "Rank")!, Is.EqualTo(1));
            Assert.That((int)Probe(asm, "RankScore", tx, "Rank", 1)!, Is.EqualTo(100));
            Assert.That((int)Probe(asm, "ItemCount", tx, "Item")!, Is.EqualTo(1));
            return Result.Ok();
        });
    }
    // ---- the ring must not retain a reverted operation ----
    [Test]
    public void RevertDropsTheChangesFromTheRingToo() {
        var (db, txType, asm) = NewDb();
        // A committed operation, so the ring has one entry that must survive.
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 1, 10, 1, 100)); return Result.Ok(); });
        // This one records two changes into the ring and is then reverted.
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 2, 20, 2, 200));
            ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 3, 30, 3, 300));
            ArmFault(tx, "Rank", 1);
            return Result.Ok();
        });
        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "RankRingCountSince", tx, "Rank", -1L)!, Is.EqualTo(1),
                "a client syncing from scratch must see only the committed change, not the two rolled back");
            Assert.That((int)Probe(asm, "RankCount", tx, "Rank")!, Is.EqualTo(1));
            return Result.Ok();
        });
    }
    [Test]
    public void RevertLeavesTheRingsEarlierEntriesInSync() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 1, 10, 1, 100)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 2, 20, 2, 200)); return Result.Ok(); });
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Rank.Update(1, (dynamic)NewRank(asm, 1, 11, 5, 555));
            ((dynamic)tx).Instant.Rank.Update(2, (dynamic)NewRank(asm, 2, 21, 6, 666));
            ArmFault(tx, "Rank", 1);
            return Result.Ok();
        });
        // The two committed inserts are still there and the two reverted updates are gone,
        // so the count must be exactly the number of surviving operations.
        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "RankRingCountSince", tx, "Rank", -1L)!, Is.EqualTo(2));
            return Result.Ok();
        });
    }
    // ---- the post-commit sweep ----
    // ---- the sweep runs after commit and must not affect the operation's result ----
    [Test]
    public void ACommittedDelete_HidesTheRowFromIterEvenBeforeTheSweep() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 1, 10, 1, 100)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 2, 20, 2, 200)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 3, 30, 3, 300)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Delete(2); return Result.Ok(); });
        // Iter walks the primary index, so the unlinked row is gone immediately - the storage
        // slot may still hold it until the sweep, and Iter must not care.
        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "RankCount", tx, "Rank")!, Is.EqualTo(2), "Iter must not surface a deleted row");
            Assert.That((bool)Probe(asm, "RankExists", tx, "Rank", 2)!, Is.False);
            Assert.That((bool)Probe(asm, "RankExists", tx, "Rank", 1)!, Is.True);
            Assert.That((bool)Probe(asm, "RankExists", tx, "Rank", 3)!, Is.True);
            return Result.Ok();
        });
    }
    [Test]
    public void ASweep_ReclaimsStorageWithoutChangingWhatIsVisible() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 1, 10, 1, 100)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 2, 20, 2, 200)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Insert((dynamic)NewRank(asm, 3, 30, 3, 300)); return Result.Ok(); });
        // Deleting the middle row forces a swap-remove: the last row is relocated into the
        // gap, so every index entry for it has to be rewritten.
        Run(db, txType, tx => { ((dynamic)tx).Instant.Rank.Delete(2); return Result.Ok(); });
        Run(db, txType, tx => {
            Assert.That((int)Probe(asm, "RankCount", tx, "Rank")!, Is.EqualTo(2));
            Assert.That((int)Probe(asm, "RankByTier", tx, "Rank", 3)!, Is.EqualTo(1), "the relocated row's index entry must follow it");
            Assert.That((bool)Probe(asm, "RankByCode", tx, "Rank", 30)!, Is.True);
            Assert.That((int)Probe(asm, "RankScore", tx, "Rank", 3)!, Is.EqualTo(300));
            Assert.That((bool)Probe(asm, "RankExists", tx, "Rank", 1)!, Is.True);
            return Result.Ok();
        });
    }
    // ---- the orphan queue across a revert ----
    [Test]
    public void ARevertedDelete_LeavesNoOrphanBehind() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 1, 5, 6, 10)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 2, 7, 8, 20)); return Result.Ok(); });
        // The delete applies and queues its slot, then the fault reverts the operation. Undo
        // has to pull that slot back out of the queue, or the sweep would later delete a
        // row that is live again.
        var reverted = Run(db, txType, tx => {
            ((dynamic)tx).Instant.Pair.Delete(1);
            ((dynamic)tx).Instant.Pair.Delete(2);
            ArmFault(tx, "Pair", 1);
            return Result.Ok();
        });

        Assert.That(reverted.GetError().Kind, Is.EqualTo(ErrorKind.ApplyFailedButRevertedSuccessfully), "precondition: the first delete applied, then reverted.");
        Settle(db, txType);
        Assert.That(PendingOrphans(db, "Pair"), Is.EqualTo(0), "a reverted delete must not leave its slot queued for reclamation");
        Assert.That((bool)ProbeOps(asm, "PairByAB", db, "Pair", 5, 6)!, Is.True);
    }
    [Test]
    public void ARevertedOperation_StillLeavesEarlierOrphansAlone() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 1, 5, 6, 10)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 2, 7, 8, 20)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 3, 9, 10, 30)); return Result.Ok(); });
        // A reverted update: it has no orphans of its own, and must not disturb anything.
        Run(db, txType, tx => {
            ((dynamic)tx).Instant.Pair.Update(1, (dynamic)NewPair(asm, 1, 50, 60, 99));
            ((dynamic)tx).Instant.Pair.Update(2, (dynamic)NewPair(asm, 2, 7, 9, 21));
            ArmFault(tx, "Pair", 1);
            return Result.Ok();
        });

        Settle(db, txType);
        Assert.That(PendingOrphans(db, "Pair"), Is.EqualTo(0));
        Assert.That((int)ProbeOps(asm, "PairCount", db, "Pair")!, Is.EqualTo(3));
        Assert.That((bool)ProbeOps(asm, "PairByAB", db, "Pair", 5, 6)!, Is.True);
    }
    [Test]
    public void ASweepClearsTheQueueSoTheNextSweepIsANoOp() {
        var (db, txType, asm) = NewDb();
        SetCleanup(db, new CleanupCollector(CleanupTrigger.PerOperation));
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 1, 5, 6, 10)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 2, 7, 8, 20)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 3, 9, 10, 30)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Delete(2); return Result.Ok(); });

        // PerOperation, so the committed delete swept immediately: the queue must be empty
        // afterwards and the surviving rows intact.
        Settle(db, txType);
        Assert.That(PendingOrphans(db, "Pair"), Is.EqualTo(0), "a completed sweep empties the queue");
        Assert.That(StorageSlots(db, "Pair"), Is.EqualTo(2), "and the slot was actually reclaimed");
        Assert.That((int)ProbeOps(asm, "PairCount", db, "Pair")!, Is.EqualTo(2));
        Assert.That((bool)ProbeOps(asm, "PairByAB", db, "Pair", 9, 10)!, Is.True, "the relocated row is still reachable");
        Assert.That((bool)ProbeOps(asm, "PairByAB", db, "Pair", 7, 8)!, Is.False);
        Assert.That((bool)ProbeOps(asm, "PairByAB", db, "Pair", 5, 6)!, Is.True);
    }
    [Test]
    public void RepeatedDeleteAndSweepCycles_LeaveEverySurvivorReachable() {
        var (db, txType, asm) = NewDb();
        SetCleanup(db, new CleanupCollector(CleanupTrigger.PerOperation));
        for (var i = 1; i <= 6; i++)
            Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, i, i * 2, i * 3, i)); return Result.Ok(); });
        // Delete from the middle repeatedly, so each sweep relocates the tail and rewrites
        // index entries. A survivor that loses its index entry here would be unreachable.
        for (var i = 1; i <= 4; i += 2)
            Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Delete(i); return Result.Ok(); });

        Settle(db, txType);
        Assert.That(PendingOrphans(db, "Pair"), Is.EqualTo(0));
        Assert.That(StorageSlots(db, "Pair"), Is.EqualTo(4));
        Assert.That((int)ProbeOps(asm, "PairCount", db, "Pair")!, Is.EqualTo(4), "6 inserted, 2 deleted");
        foreach (var i in new[] { 2, 4, 5, 6 })
            Assert.That((bool)ProbeOps(asm, "PairByAB", db, "Pair", i * 2, i * 3)!, Is.True, $"row {i} must still resolve");
    }
    // ---- orphans must survive until a sweep actually reclaims them ----
    [Test]
    public void AnOrphanQueuedByOneOperation_SurvivesTheNextWriteToTheSameTable_UntilASweepReclaimsIt() {
        // PerTime is the production default, so most operations don't sweep. An interval no
        // clock reaches makes that "none" here - deterministic.
        var (db, txType, asm) = NewDb();
        SetCleanup(db, new CleanupCollector(CleanupTrigger.PerTime, TimeSpan.FromDays(36500)));
        for (var i = 1; i <= 3; i++)
            Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, i, i * 2, i * 3, i)); return Result.Ok(); });
        Assert.That(Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Delete(2); return Result.Ok(); }).IsOk(), Is.True);
        Settle(db, txType);
        Assert.That(PendingOrphans(db, "Pair"), Is.EqualTo(1), "precondition: queued, not swept");
        Assert.That(StorageSlots(db, "Pair"), Is.EqualTo(3));

        Assert.That(Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 4, 8, 12, 4)); return Result.Ok(); }).IsOk(), Is.True);

        Assert.Multiple(() => {
            Settle(db, txType);
            Assert.That(PendingOrphans(db, "Pair"), Is.EqualTo(1), "the next write to the table must not forget a slot nobody has reclaimed yet");
            Assert.That(StorageSlots(db, "Pair"), Is.EqualTo((int)ProbeOps(asm, "PairCount", db, "Pair")! + PendingOrphans(db, "Pair")),
                "every storage slot is either a live row or queued for reclamation - none silently dead");

            Assert.That(Run(db, txType, tx => { ((dynamic)T(tx, "Pair")).SweepDeleted(); return Result.Ok(); }).IsOk(), Is.True);

            Settle(db, txType);
            Assert.That(PendingOrphans(db, "Pair"), Is.EqualTo(0));
            Assert.That(StorageSlots(db, "Pair"), Is.EqualTo(3), "the sweep reclaimed the dead slot");
            foreach (var i in new[] { 1, 3, 4 })
                Assert.That((bool)ProbeOps(asm, "PairByAB", db, "Pair", i * 2, i * 3)!, Is.True, $"row {i} must still resolve");
        });
    }
    [Test]
    public void ASweepOfAnUnusuallyLargeBatch_GivesTheQueuesCapacityBack_AndASmallOneKeepsIt() {
        var (db, txType, asm) = NewDb();
        SetCleanup(db, new CleanupCollector(CleanupTrigger.PerOperation));
        Run(db, txType, tx => {
            for (var i = 1; i <= 1600; i++) ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, i, i, -i, i));
            return Result.Ok();
        });
        var queue = (HashSet<int>)OpsOf(db, "Pair").GetType().GetField("orphanOffsets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(OpsOf(db, "Pair"))!;

        Run(db, txType, tx => { for (var i = 1; i <= 20; i++) ((dynamic)tx).Instant.Pair.Delete(i); return Result.Ok(); });
        Settle(db, txType);
        var afterSmall = queue.EnsureCapacity(0);

        Run(db, txType, tx => { for (var i = 21; i <= 1500; i++) ((dynamic)tx).Instant.Pair.Delete(i); return Result.Ok(); });
        Settle(db, txType);

        Assert.That(afterSmall, Is.GreaterThanOrEqualTo(20), "a small sweep keeps its capacity - the next deletes reuse it without allocating");
        Assert.That(queue.Count, Is.EqualTo(0));
        Assert.That(queue.EnsureCapacity(0), Is.LessThan(1480), "a sweep past the threshold must not keep ~1.5k slots of capacity around forever");
        Assert.That(StorageSlots(db, "Pair"), Is.EqualTo(100));
        foreach (var i in new[] { 1501, 1550, 1600 })
            Assert.That((bool)ProbeOps(asm, "PairByAB", db, "Pair", i, -i)!, Is.True, $"row {i} must still resolve");
    }
    // ---- an unrevertible failure must not sweep ----
    [Test]
    public void AFailedRevert_ReportsApplyFailedAndLeavesTheSweepAlone() {
        var (db, txType, asm) = NewDb();
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 1, 5, 6, 10)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 2, 7, 8, 20)); return Result.Ok(); });
        // The delete applies and queues its slot; the apply fault fires; and the revert is
        // then made to fail too. That is the only route to the unrevertible state, and it
        // must be reported as ApplyFailed rather than the recoverable kind.
        var failed = Run(db, txType, tx => {
            ((dynamic)tx).Instant.Pair.Delete(1);
            ArmFault(tx, "Pair", 0);
            ArmRevertFault(tx, "Pair");
            return Result.Ok();
        });
        Assert.That(failed.IsError(), Is.True);
        Assert.That(failed.GetError().Kind, Is.EqualTo(ErrorKind.ApplyFailed),
            "a revert that itself failed is not a recoverable outcome");
    }
    [Test]
    public void AFailedRevert_DoesNotSweepTheQueuedOrphans() {
        var (db, txType, asm) = NewDb();
        SetCleanup(db, new CleanupCollector(CleanupTrigger.PerOperation));
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 1, 5, 6, 10)); return Result.Ok(); });
        Run(db, txType, tx => { ((dynamic)tx).Instant.Pair.Insert((dynamic)NewPair(asm, 2, 7, 8, 20)); return Result.Ok(); });
        // The fault fires on the second change, so the first delete has already applied and
        // queued its slot; the revert then fails too, leaving that slot in the queue.
        var failed = Run(db, txType, tx => {
            ((dynamic)tx).Instant.Pair.Delete(1);
            ((dynamic)tx).Instant.Pair.Delete(2);
            ArmFault(tx, "Pair", 1);
            ArmRevertFault(tx, "Pair");
            return Result.Ok();
        });

        // The sweep gate is IsOkOrReverted, and ApplyFailed is neither - even PerOperation must
        // not reclaim. Read off the database directly: it's poisoned, so no further Run gets in.
        Assert.That(failed.GetError().Kind, Is.EqualTo(ErrorKind.ApplyFailed));
        Settle(db, txType);
        Assert.That(PendingOrphans(db, "Pair"), Is.GreaterThan(0),
            "an unrevertible failure must not reclaim storage - the state is unknown");
    }
}
