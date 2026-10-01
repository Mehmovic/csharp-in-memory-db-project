using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// Revert coverage for persistent tables. OperationRevertTests and
// OperationRevertIndexTests are entirely TableKind.Instant, and that left the
// durable half of the revert path untested - a persistent Apply does one thing
// an instant Apply does not: cold.Stage(...) every insert/update/delete into the
// cold store's operation staging buffer, on its way to the WAL and the next
// checkpoint. Revert restores memory and indexes; discarding that staged copy is
// EndScope(commit: false)'s job, and nothing proved it happens.
//
// The test that matters most here is ARevertedPersistentOperation_StagesNothing
// ForTheNextOperation: it commits a *later* operation and reopens, so a leak
// would resurface through recovery as a row that was never supposed to exist.
//
// Same seams as the instant tests - TestOnlyApplyFault is the only way to throw
// from inside the generated Apply, and QuerySet/QuerySingle are ref structs so
// reads go through statically-compiled helpers in the fixture.
public class OperationRevertPersistentTests {
    private string dir = "";

    private const string Source = """
        using System;
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class PRDb : DbContext<PRDbTransaction> { }

        [Table(TableKind.Persistent, typeof(PRDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Vault(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance,
            [Index(IndexKind.Hash, Uniqueness.NonUnique)] [property: MemoryPackOrder(2)] [property: Key(2)] int ClubId);

        // Same operation, second table, opposite kind - proves the transaction-level
        // revert loop spans both kinds rather than only the ones it was written for.
        [Table(TableKind.Instant, typeof(PRDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Tally(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int Score);

        // Evictable: Evict stages a candidate that is applied at the NEXT operation
        // boundary, not during Apply, so an eviction and a revert interact across two
        // operations rather than one.
        [Table(TableKind.Persistent, typeof(PRDb), Evictable = true)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Crate(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int Qty);

        public static class Probe {
            public static decimal VaultBalance(PRDbVaultOps v, int id) => v.Primary.Find(id).Get().Unwrap().Balance;
            public static bool VaultExists(PRDbVaultOps v, int id) => v.Primary.Find(id).HasRow();
            public static int VaultCount(PRDbVaultOps v) { using var q = v.Iter(); return q.Get().Unwrap().Length; }
            public static int VaultClubCount(PRDbVaultOps v, int clubId) { using var q = v.Idx.ClubId.Find(clubId); return q.Count; }
            public static int TallyScore(PRDbTallyOps t, int id) => t.Primary.Find(id).Get().Unwrap().Score;
            public static bool TallyExists(PRDbTallyOps t, int id) => t.Primary.Find(id).HasRow();

            public static bool CrateInMemory(PRDbCrateOps c, int id) => c.Primary.Find(id).HasRow();
            public static int CrateQty(PRDbCrateOps c, int id) => c.Primary.Find(id).Get().Unwrap().Qty;
            public static int CratePeeked(PRDbCrateOps c, int id) {
                var r = c.Storage.Peek(id);
                return r.IsError() ? -1 : r.Unwrap().Qty;
            }
            public static int CrateLoaded(PRDbCrateOps c, int id) => c.Storage.Load(id).IsOk() ? c.Primary.Find(id).Get().Unwrap().Qty : -1;
        }
        """;
[SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-revert-persistent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static private (object Db, Type TxType, System.Reflection.Assembly Asm) NewDb(ColdStore cold) {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        return (Activator.CreateInstance(asm.GetType("TestNs.PRDb")!, cold)!, asm.GetType("TestNs.PRDbTransaction")!, asm);
    }

    static private object NewVault(System.Reflection.Assembly asm, int id, decimal balance, int clubId)
        => Activator.CreateInstance(asm.GetType("TestNs.Vault")!, id, balance, clubId)!;

    static private object NewTally(System.Reflection.Assembly asm, int id, int score)
        => Activator.CreateInstance(asm.GetType("TestNs.Tally")!, id, score)!;

    static private object NewCrate(System.Reflection.Assembly asm, int id, int qty)
        => Activator.CreateInstance(asm.GetType("TestNs.Crate")!, id, qty)!;

    // The transaction exposes each table accessor as a public readonly *field*, not a
    // property - see the note in OperationRevertTests.T.
    static private object T(object tx, string accessor) {
        var type = tx.GetType();
        var field = type.GetField(accessor);
        return field is not null ? field.GetValue(tx)! : type.GetProperty(accessor)!.GetValue(tx)!;
    }

    static private Result Run(object db, Type txType, Func<object, object, Result> body, PropagationMode mode = PropagationMode.Optimistic)
        => (Result)((Task<Result>)GeneratorTestHost.RunTransactional(db, txType, (ctx, tx) => body(tx, txType), mode)).Result;

    static private object? Probe(System.Reflection.Assembly asm, string method, object tx, string accessor, params object?[] args)
        => GeneratorTestHost.InvokeHelper(asm, "TestNs.Probe", method, [T(tx, accessor), .. args]);

    // One-shot fault at a given change ordinal - see OperationRevertTests.ArmFault
    // for why the ops object must be disarmed rather than left armed.
    static private void ArmFault(object tx, string accessor, int throwAt) {
        var ops = T(tx, accessor);
        var prop = ops.GetType().GetProperty("TestOnlyApplyFault")!;
        prop.SetValue(ops, (Action<int>)(i => {
            if (i != throwAt) return;
            prop.SetValue(ops, null);
            throw new InvalidOperationException($"injected fault at change {i}");
        }));
    }
// ---- the durable half of revert ----

    // A persistent Apply calls cold.Stage(...) for every change on its way to the WAL.
    // Revert restores memory and indexes; discarding that staged copy is
    // EndScope(commit: false)'s job. If it leaked, the leak would be invisible in
    // memory and would resurface later as a row that never existed - so this test
    // commits a *different* operation afterwards and reopens, letting recovery replay
    // whatever reached the WAL.
    [Test]
    public void ARevertedPersistentOperation_StagesNothingForTheNextOperation() {
        System.Reflection.Assembly asm;
        Type txType;

        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (var db, var tx, var a) = NewDb(cold);
            asm = a; txType = tx;

            Run(db, tx, (t, _) => { ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 1, 100m, 7)); return Result.Ok(); },
                PropagationMode.Confirmed);

            // Reverted: an update that lands, an insert that lands, then a fault.
            var reverted = Run(db, tx, (t, _) => {
                ((dynamic)t).Vault.Update(1, (dynamic)NewVault(asm, 1, 999m, 7));
                ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 2, 555m, 7));
                ArmFault(t, "Vault", 1);
                return Result.Ok();
            }, PropagationMode.Confirmed);

            Assert.That(reverted.GetError().Kind, Is.EqualTo(ErrorKind.ApplyFailedButRevertedSuccessfully),
                "the operation failed but was reverted, so the database stays healthy");

            // A later, entirely separate operation - if the reverted changes were still
            // sitting in the staging buffer, EndScope(commit: true) would sweep them
            // into this operation's WAL entry.
            Run(db, tx, (t, _) => { ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 3, 42m, 8)); return Result.Ok(); },
                PropagationMode.Confirmed);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        reopenedCold.CompleteRecovery();
        var (reopenedDb, reopenedTx, _) = NewDb(reopenedCold);

        Run(reopenedDb, reopenedTx, (t, _) => {
            // 1 survived from before the fault, 3 from the later operation. 2 was
            // inserted by the reverted operation and must not exist anywhere.
            Assert.That((bool)Probe(asm, "VaultExists", t, "Vault", 1)!, Is.True, "the original row is durable");
            Assert.That((decimal)Probe(asm, "VaultBalance", t, "Vault", 1)!, Is.EqualTo(100m),
                "the reverted update never reached durable storage");
            Assert.That((bool)Probe(asm, "VaultExists", t, "Vault", 2)!, Is.False,
                "the row inserted by the reverted operation must not survive a reopen");
            Assert.That((bool)Probe(asm, "VaultExists", t, "Vault", 3)!, Is.True, "the later operation committed");
            return Result.Ok();
        });
    }
// In-memory revert on a persistent table: values, existence and index counts all
    // restored. The instant suite proves this for instant tables; persistent Apply
    // runs the same journal but also stages to cold, so it is worth pinning on its own.
    [Test]
    public void AFaultInAPersistentTable_RestoresItsRowsAndIndexEntries() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        Run(db, txType, (t, _) => { ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 1, 100m, 7)); return Result.Ok(); });

        Run(db, txType, (t, _) => {
            ((dynamic)t).Vault.Update(1, (dynamic)NewVault(asm, 1, 999m, 7));
            ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 2, 555m, 7));
            ArmFault(t, "Vault", 1);
            return Result.Ok();
        });

        Run(db, txType, (t, _) => {
            Assert.That((bool)Probe(asm, "VaultExists", t, "Vault", 1)!, Is.True, "the original row survives");
            Assert.That((decimal)Probe(asm, "VaultBalance", t, "Vault", 1)!, Is.EqualTo(100m), "balance reverted");
            Assert.That((bool)Probe(asm, "VaultExists", t, "Vault", 2)!, Is.False, "the inserted row was lifted back out");
            Assert.That((int)Probe(asm, "VaultCount", t, "Vault")!, Is.EqualTo(1));
            Assert.That((int)Probe(asm, "VaultClubCount", t, "Vault", 7)!, Is.EqualTo(1),
                "the non-unique index entry inserted by the reverted operation is gone");
            return Result.Ok();
        });
    }

    // The transaction reverts in reverse table order, across kinds. Tally is instant
    // and applied first; the fault is in the persistent Vault that follows, so Tally's
    // already-applied changes must be rolled back too.
    [Test]
    public void AFaultInThePersistentTable_AlsoRevertsTheInstantTablesChanges() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        Run(db, txType, (t, _) => { ((dynamic)t).Tally.Insert((dynamic)NewTally(asm, 1, 10)); return Result.Ok(); });

        Run(db, txType, (t, _) => {
            ((dynamic)t).Tally.Update(1, (dynamic)NewTally(asm, 1, 88));
            ((dynamic)t).Tally.Insert((dynamic)NewTally(asm, 2, 20));
            ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 1, 500m, 3));
            ArmFault(t, "Vault", 0);
            return Result.Ok();
        });

        Run(db, txType, (t, _) => {
            Assert.That((int)Probe(asm, "TallyScore", t, "Tally", 1)!, Is.EqualTo(10), "instant table update reverted");
            Assert.That((bool)Probe(asm, "TallyExists", t, "Tally", 2)!, Is.False, "instant table insert reverted");
            Assert.That((bool)Probe(asm, "VaultExists", t, "Vault", 1)!, Is.False, "the faulting table reverted too");
            return Result.Ok();
        });
    }

    // A failed operation must leave the database able to accept the next one - for a
    // persistent table that also means not being poisoned, since poison is sticky and
    // DbContext.PoisonDatabase is what sets it.
    [Test]
    public void ARevertedPersistentOperation_LeavesTheDatabaseUsableForTheNextOperation() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        Run(db, txType, (t, _) => {
            ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 1, 10m, 1));
            ArmFault(t, "Vault", 0);
            return Result.Ok();
        });

        Run(db, txType, (t, _) => {
            ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 2, 20m, 2));
            return Result.Ok();
        }, PropagationMode.Confirmed);

        Run(db, txType, (t, _) => {
            Assert.That((bool)Probe(asm, "VaultExists", t, "Vault", 2)!, Is.True,
                "the database was not poisoned, so the next operation committed normally");
            Assert.That((int)Probe(asm, "VaultCount", t, "Vault")!, Is.EqualTo(1),
                "only the surviving row is present");
            return Result.Ok();
        });
    }

    // A delete on a persistent table stages a Delete to cold and unlinks every index
    // entry. Reverting must put the row and its entries back, and must also pull the
    // offset out of the orphan queue - otherwise the post-commit sweep would delete a
    // row that is live again.
    [Test]
    public void ARevertedPersistentDelete_PutsTheRowAndItsIndexBack() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        Run(db, txType, (t, _) => {
            ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 1, 250m, 4));
            ((dynamic)t).Vault.Insert((dynamic)NewVault(asm, 2, 260m, 4));
            return Result.Ok();
        });

        Run(db, txType, (t, _) => {
            // Two deletes so that change index 1 exists: change 0 (the first delete)
            // applies, then the fault stops change 1 - which is the only way to reach
            // a journaled delete inside RevertUndo.
            ((dynamic)t).Vault.Delete(1);
            ((dynamic)t).Vault.Delete(2);
            ArmFault(t, "Vault", 1);
            return Result.Ok();
        });

        Run(db, txType, (t, _) => {
            Assert.That((bool)Probe(asm, "VaultExists", t, "Vault", 1)!, Is.True, "the deleted row is back");
            Assert.That((decimal)Probe(asm, "VaultBalance", t, "Vault", 1)!, Is.EqualTo(250m), "with its original value");
            Assert.That((int)Probe(asm, "VaultCount", t, "Vault")!, Is.EqualTo(2), "both rows present");
            Assert.That((int)Probe(asm, "VaultClubCount", t, "Vault", 4)!, Is.EqualTo(2),
                "its non-unique index entry was re-inserted by the revert");
            return Result.Ok();
        });
    }

// ---- eviction, which spans two operations ----

    // Evict(id) only *stages* a candidate (cold.StageEviction); the mdbx write-through
    // and the memory drop happen at the NEXT operation's boundary
    // (ColdStore.BeginScope -> ApplyPendingEvictions), and TryGetCurrentRowBytesForEviction
    // reads the row's value at that moment rather than at Evict time.
    //
    // So an eviction staged in a reverted operation must persist the POST-revert value.
    // The update below is applied and then reverted; if the eviction captured either the
    // pre-revert value or the reverted-away 999, cold storage would hold a value that no
    // operation ever committed.
    [Test]
    public void AnEvictionStagedInARevertedOperation_PersistsThePostRevertValue() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        Run(db, txType, (t, _) => { ((dynamic)t).Crate.Insert((dynamic)NewCrate(asm, 1, 100)); return Result.Ok(); });

        // Evict stages; Update(0) applies; the fault stops change 1 so the update reverts.
        var reverted = Run(db, txType, (t, _) => {
            ((dynamic)t).Crate.Storage.Evict(1);
            ((dynamic)t).Crate.Update(1, (dynamic)NewCrate(asm, 1, 999));
            ((dynamic)t).Crate.Insert((dynamic)NewCrate(asm, 2, 5));
            ArmFault(t, "Crate", 1);
            return Result.Ok();
        });
        Assert.That(reverted.GetError().Kind, Is.EqualTo(ErrorKind.ApplyFailedButRevertedSuccessfully));

        // This operation's boundary applies the eviction batch. Whatever it wrote must
        // be the value the revert restored, not the 999 the update briefly held.
        Run(db, txType, (t, _) => {
            Assert.That((bool)Probe(asm, "CrateInMemory", t, "Crate", 1)!, Is.False,
                "the eviction batch applied at this boundary, so the row left memory");
            Assert.That((int)Probe(asm, "CratePeeked", t, "Crate", 1)!, Is.EqualTo(100),
                "cold storage must hold the post-revert value, not the reverted-away 999");
            Assert.That((int)Probe(asm, "CrateLoaded", t, "Crate", 1)!, Is.EqualTo(100),
                "Load brings back the same post-revert value");
            Assert.That((bool)Probe(asm, "CrateInMemory", t, "Crate", 2)!, Is.False,
                "the insert from the reverted operation is gone, so the batch had no row to write for it");
            return Result.Ok();
        });
    }

    // The same interaction across a restart: the eviction's write-through copy is the
    // only durable record of a row that has left memory, so a revert that corrupted it
    // would survive the process and only be visible later.
    [Test]
    public void AnEvictionStagedInARevertedOperation_SurvivesRestartWithTheRevertedValue() {
        System.Reflection.Assembly asm;
        Type txType;

        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var (db, tx, a) = NewDb(cold);
            asm = a; txType = tx;

            Run(db, tx, (t, _) => { ((dynamic)t).Crate.Insert((dynamic)NewCrate(asm, 1, 100)); return Result.Ok(); }, PropagationMode.Confirmed);

            Run(db, tx, (t, _) => {
                ((dynamic)t).Crate.Storage.Evict(1);
                ((dynamic)t).Crate.Update(1, (dynamic)NewCrate(asm, 1, 999));
                ((dynamic)t).Crate.Insert((dynamic)NewCrate(asm, 2, 5));
                ArmFault(t, "Crate", 1);
                return Result.Ok();
            }, PropagationMode.Confirmed);

            // A boundary operation so the eviction batch actually applies, plus an
            // unrelated commit so the WAL carries something recovery must replay.
            Run(db, tx, (t, _) => { ((dynamic)t).Crate.Insert((dynamic)NewCrate(asm, 3, 7)); return Result.Ok(); },
                PropagationMode.Confirmed);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        reopenedCold.CompleteRecovery();
        var (reopenedDb, reopenedTx, _) = NewDb(reopenedCold);

        Run(reopenedDb, reopenedTx, (t, _) => {
            Assert.That((int)Probe(asm, "CratePeeked", t, "Crate", 1)!, Is.EqualTo(100),
                "after recovery the durable copy of the evicted row is the post-revert value");
            Assert.That((int)Probe(asm, "CrateLoaded", t, "Crate", 1)!, Is.EqualTo(100),
                "and it loads back with that same value");
            Assert.That((bool)Probe(asm, "CrateInMemory", t, "Crate", 2)!, Is.False,
                "the reverted insert must not come back through recovery");
            return Result.Ok();
        });
    }
}
