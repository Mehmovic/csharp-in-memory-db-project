using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Milestone 3: TableKind.Persistent + .Storage (Load/Evict/Peek) wired
// through the generated Apply() path. These tests exercise real
// libmdbx-backed durability through the generated path directly - as of
// 2026-09-12, Persistent-kind tables inline their own storage/ColdTable
// fields the same way Instant-kind tables always have (see
// TableGenerator.cs's file-level comment) - PersistentTable<TKey,TRow>,
// the hand-written engine that used to sit in between, is gone.
public class Milestone3Tests {
    private string dir = "";

    // Account is deliberately all-unmanaged fields (no string) - MemoryPack
    // serializes unmanaged structs automatically, with no [MemoryPackable]
    // attribute or generated formatter needed, which sidesteps a real
    // MemoryPack.Generator/Roslyn-preview interop gap this harness hit when
    // trying to run that generator manually alongside TableGenerator (see
    // git history around 2026-09-12 if this needs revisiting for a
    // reference-typed persistent-table field).
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class BankDb : DbContext<BankDbTransaction> { }

        [Table(TableKind.Persistent, typeof(BankDb), Evictable = true)]
        public readonly partial record struct Account([PrimaryKey] int Id, int OwnerId, decimal Balance);

        [Table(TableKind.Instant, typeof(BankDb))]
        public readonly partial record struct Player([PrimaryKey] int Id, string Name);
        """;

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-milestone3-tests", Guid.NewGuid().ToString("N"));
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

    static private object NewAccount(System.Reflection.Assembly assembly, int id, int ownerId, decimal balance) {
        var t = assembly.GetType("TestNs.Account")!;
        return Activator.CreateInstance(t, id, ownerId, balance)!;
    }

    static private object NewPlayer(System.Reflection.Assembly assembly, int id, string name) {
        var t = assembly.GetType("TestNs.Player")!;
        return Activator.CreateInstance(t, id, name)!;
    }

    [Test]
    public async Task Insert_ThenGet_ReturnsTheInsertedRow() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m));
                found = dtx.Account.Get(1).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }

    [Test]
    public async Task Insert_DuplicatePrimaryKey_FailsValidation() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 2, 50m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public async Task Update_ThenGet_ReturnsTheUpdatedRow() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 1, 250m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        decimal balance = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { balance = (decimal)((dynamic)tx).Account.Get(1).Unwrap().Balance; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(balance, Is.EqualTo(250m));
    }

    [Test]
    public async Task Delete_ThenGet_ReportsNotFound() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Delete(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Account.Get(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.False);
    }

    // ---- The core durability promise: survives a real close + reopen, through the generated path ----

    [Test]
    public async Task Insert_Confirmed_ThenCloseAndReopen_StorageLoadRoundTripsTheRow() {
        System.Reflection.Assembly asm;
        Type dbType, txType;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
            dbType = asm.GetType("TestNs.BankDb")!;
            txType = asm.GetType("TestNs.BankDbTransaction")!;
            var db = Activator.CreateInstance(dbType, cold)!;

            var insert = await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
                PropagationMode.Confirmed);
            Assert.That(insert.IsOk(), Is.True);
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;

        var loaded = false;
        decimal balance = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => {
                dynamic dtx = tx;
                loaded = dtx.Account.Storage.Load(1).IsOk();
                balance = (decimal)dtx.Account.Get(1).Unwrap().Balance;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(loaded, Is.True);
        Assert.That(balance, Is.EqualTo(100m));
    }

    [Test]
    public async Task Evict_ThenGet_ReportsNotFound_ButStorageLoadRestoresIt() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        var evicted = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { evicted = ((dynamic)tx).Account.Storage.Evict(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);
        Assert.That(evicted, Is.True);

        var foundAfterEvict = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { foundAfterEvict = ((dynamic)tx).Account.Get(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);
        Assert.That(foundAfterEvict, Is.False, "Get must never implicitly reload from cold storage.");

        var loaded = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { loaded = ((dynamic)tx).Account.Storage.Load(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);
        Assert.That(loaded, Is.True);
    }

    [Test]
    public async Task Peek_AnEvictedRow_ReturnsItsValueWithoutReloadingIntoMemory() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Storage.Evict(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        decimal peekedBalance = -1;
        var stillNotInMemory = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                peekedBalance = (decimal)dtx.Account.Storage.Peek(1).Unwrap().Balance;
                stillNotInMemory = !dtx.Account.Get(1).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(peekedBalance, Is.EqualTo(100m));
        Assert.That(stillNotInMemory, Is.True, "Peek must not cache the row into memory.");
    }

    // ---- Cross-kind atomicity: an Instant table's insert must not survive a Persistent table's Validate() failure in the same operation ----

    [Test]
    public async Task CrossKindAtomicity_APersistentTablesValidationFailure_LeavesAnInstantTablesInsertUnapplied() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        // Pre-seed account 1 so a colliding Id fails Accounts' Validate().
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 1, 100m)); return Result.Ok(); },
            PropagationMode.Optimistic);

        // One operation: Players' insert would succeed on its own; Accounts'
        // insert collides. Accounts is declared first (see Source), so its
        // Validate() failure must stop the whole operation before either
        // table's Apply() runs.
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Insert((dynamic)NewPlayer(asm, 1, "Alice"));
                dtx.Account.Insert((dynamic)NewAccount(asm, 1, 2, 200m));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);

        var playerFound = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { playerFound = ((dynamic)tx).Player.Get(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(playerFound, Is.False,
            "Players' staged insert must not survive Accounts' Validate() failure in the same operation.");
    }
}
