using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Secondary indexes on Persistent-kind tables (the RHINO006 restriction is
// gone). As of 2026-09-12, Persistent-kind Ops classes inline their own
// storage/primaryIndex fields directly (PersistentTable<TKey,TRow>, the
// engine that used to sit in between, is gone entirely) - so index
// maintenance here is byte-for-byte the same code path Instant-kind tables
// already use, plus a ColdTable/ColdStore write-through in Apply().
public class PersistentSecondaryIndexTests {
    private string dir = "";

    // AccountNumber/OwnerId are deliberately int, not string - see
    // Milestone3Tests.cs's Account for why (MemoryPack.Generator interop gap
    // sidestepped by keeping every persistent-table row fully unmanaged).
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class BankDb : DbContext<BankDbTransaction> { }

        [Table(TableKind.Persistent, typeof(BankDb), Evictable = true)]
        public readonly partial record struct Account(
            [PrimaryKey] int Id,
            [Index(IndexKind.Hash, Uniqueness.Unique)] int AccountNumber,
            [Index(IndexKind.Hash, Uniqueness.NonUnique)] int OwnerId,
            decimal Balance);
        """;

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-persistent-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static private (object Db, Type TxType, System.Reflection.Assembly Assembly) NewDb(ColdStore cold) {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.BankDb")!;
        var txType = asm.GetType("TestNs.BankDbTransaction")!;
        var db = Activator.CreateInstance(dbType, cold)!;
        return (db, txType, asm);
    }

    static private object NewAccount(System.Reflection.Assembly assembly, int id, int accountNumber, int ownerId, decimal balance) {
        var t = assembly.GetType("TestNs.Account")!;
        return Activator.CreateInstance(t, id, accountNumber, ownerId, balance)!;
    }

    [Test]
    public async Task UniqueIndex_InsertThenReadInASeparateOperation_FindsTheRow() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1001, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Account.AccountNumber(1001).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }

    [Test]
    public async Task UniqueIndex_InsertingADuplicateValue_FailsValidation() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1001, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 2, 1001, 2, 50m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public async Task NonUniqueIndex_TwoAccountsUnderOneOwner_BothResolveInASeparateOperation() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Account.Insert((dynamic)NewAccount(asm, 1, 1001, 10, 100m));
                dtx.Account.Insert((dynamic)NewAccount(asm, 2, 1002, 10, 50m));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var count = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { count = ((dynamic)tx).Account.OwnerId(10).Count; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task UniqueIndex_UpdatingTheIndexedField_MakesTheOldValueUnresolvable() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1001, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 2002, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        bool oldFound = true, newFound = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                oldFound = dtx.Account.AccountNumber(1001).IsOk();
                newFound = dtx.Account.AccountNumber(2002).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(oldFound, Is.False);
        Assert.That(newFound, Is.True);
    }

    [Test]
    public async Task DeleteForcingASwapRemove_RelocatedRowsUniqueIndexEntryStillResolves() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Account.Insert((dynamic)NewAccount(asm, 1, 1001, 1, 100m));
                dtx.Account.Insert((dynamic)NewAccount(asm, 2, 2002, 2, 200m));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        // Deleting offset 0 forces DenseArray.Delete to relocate the
        // physically-last row (account 2, at offset 1) into the freed slot -
        // its unique-index entry must be repointed to the new offset.
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Delete(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Account.AccountNumber(2002).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True, "The swap-relocated row's unique-index entry must still resolve at its new offset.");
    }

    [Test]
    public async Task StorageEvict_RemovesTheSecondaryIndexEntry_StorageLoadRestoresIt() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1001, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Storage.Evict(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var foundAfterEvict = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { foundAfterEvict = ((dynamic)tx).Account.AccountNumber(1001).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);
        Assert.That(foundAfterEvict, Is.False, "Evicting the row must also remove its now-stale secondary-index entry.");

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Storage.Load(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var foundAfterLoad = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { foundAfterLoad = ((dynamic)tx).Account.AccountNumber(1001).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);
        Assert.That(foundAfterLoad, Is.True, "Loading the row back must re-register its secondary-index entry exactly once.");
    }

    [Test]
    public async Task StorageLoad_CalledTwice_DoesNotDoubleInsertIntoTheUniqueIndex() {
        // If LoadInternal inserted into the secondary index unconditionally
        // (instead of only when the row was NOT already loaded), a second
        // Load call against an already-loaded row would double-insert into
        // a HashIndex-backed unique index - this proves it doesn't.
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1001, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Account.Storage.Load(1);
                dtx.Account.Storage.Load(1);
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public async Task DurabilityRoundTrip_StorageLoadAfterReopen_RestoresTheSecondaryIndexToo() {
        System.Reflection.Assembly asm;
        Type dbType, txType;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            dbType = asm.GetType("TestNs.BankDb")!;
            txType = asm.GetType("TestNs.BankDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;

            var insert = await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1001, 1, 100m)); return Result.Ok(); },
                PropagationMode.Confirmed);
            Assert.That(insert.IsOk(), Is.True);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => { ((dynamic)tx).Account.Storage.Load(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => { found = ((dynamic)tx).Account.AccountNumber(1001).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }
}
