using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Milestone 2: secondary indexes (unique + non-unique) wired into the
// generated Ops class, and real pre-apply Validate() proving cross-table
// atomicity - a second table's Validate() failure must leave a first,
// individually-valid table's staged insert unapplied, with no rollback
// machinery needed (see Docs/02-architecture.md § Transactions).
public class Milestone2Tests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class ShopDb : DbContext<ShopDbTransaction> { }

        [Table(TableKind.Instant, typeof(ShopDb))]
        public readonly partial record struct Club(
            [PrimaryKey] int Id,
            string Name,
            [Index(IndexKind.Hash, Uniqueness.Unique)] string ShortCode);

        [Table(TableKind.Instant, typeof(ShopDb))]
        public readonly partial record struct Player(
            [PrimaryKey] int Id,
            string Name,
            [Index(IndexKind.Hash, Uniqueness.NonUnique)] int ClubId);

        [Table(TableKind.Instant, typeof(ShopDb))]
        public readonly partial record struct Fixture(
            [PrimaryKey] int Id,
            [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "HomeAway", Order = 1)] int HomeClubId,
            [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "HomeAway", Order = 0)] int AwayClubId);
        """;

    static private (object Db, Type TxType, System.Reflection.Assembly Assembly) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.ShopDb")!;
        var txType = asm.GetType("TestNs.ShopDbTransaction")!;
        var db = Activator.CreateInstance(dbType)!;
        return (db, txType, asm);
    }

    static private object NewClub(System.Reflection.Assembly assembly, int id, string name, string shortCode) {
        var t = assembly.GetType("TestNs.Club")!;
        return Activator.CreateInstance(t, id, name, shortCode)!;
    }

    static private object NewPlayer(System.Reflection.Assembly assembly, int id, string name, int clubId) {
        var t = assembly.GetType("TestNs.Player")!;
        return Activator.CreateInstance(t, id, name, clubId)!;
    }

    static private object NewFixture(System.Reflection.Assembly assembly, int id, int homeClubId, int awayClubId) {
        var t = assembly.GetType("TestNs.Fixture")!;
        return Activator.CreateInstance(t, id, homeClubId, awayClubId)!;
    }

    // ---- Unique secondary index ----

    [Test]
    public async Task UniqueSecondaryIndex_ShortCode_FindsTheInsertedRow() {
        // Two separate operations, deliberately: ShortCode reads the raw
        // secondary index directly, not overlay-aware yet (Milestone 4's
        // job - see the generator's comment on the accessor methods), so
        // it only sees a row once Apply() has actually run.
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Clubs.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Clubs.ShortCode("ARS").IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }

    [Test]
    public async Task UniqueSecondaryIndex_InsertingADuplicateValue_FailsValidation() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Clubs.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Clubs.Insert((dynamic)NewClub(asm, 2, "Arsenal Reserves", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public async Task UniqueSecondaryIndex_UpdatingAnUnrelatedField_DoesNotFalselyCollideWithItself() {
        // The self-offset-aware check: updating a row that already owns its
        // own unique value must not see that value as a conflict against itself.
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Clubs.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Clubs.Update(1, (dynamic)NewClub(asm, 1, "Arsenal FC", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
    }

    // ---- Non-unique secondary index ----

    [Test]
    public async Task NonUniqueSecondaryIndex_ClubId_ReturnsAllMatchingRows() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Players.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10));
                dtx.Players.Insert((dynamic)NewPlayer(asm, 2, "Bob", 10));
                dtx.Players.Insert((dynamic)NewPlayer(asm, 3, "Carol", 20));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var team10Count = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { team10Count = ((dynamic)tx).Players.ClubId(10).Count; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(team10Count, Is.EqualTo(2));
    }

    [Test]
    public async Task NonUniqueSecondaryIndex_InsertingASharedValueTwice_BothSucceed() {
        var (db, txType, asm) = NewDb();

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Players.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10));
                dtx.Players.Insert((dynamic)NewPlayer(asm, 2, "Bob", 10));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
    }

    // ---- Same-batch staged-duplicate detection (a real gap the Milestone 1
    // stub Validate() => true left open) ----

    [Test]
    public async Task TwoInsertsOfTheSameKey_WithinOneOperation_FailsValidationInsteadOfSilentlyCorrupting() {
        var (db, txType, asm) = NewDb();

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Players.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10));
                dtx.Players.Insert((dynamic)NewPlayer(asm, 1, "AliceAgain", 10));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    // ---- Cross-table atomicity ----

    [Test]
    public async Task CrossTableAtomicity_ASecondTablesValidationFailure_LeavesTheFirstTablesInsertUnapplied() {
        var (db, txType, asm) = NewDb();

        // Pre-seed Arsenal so a colliding ShortCode fails Clubs' Validate().
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Clubs.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        // One operation: Players' insert would succeed on its own; Clubs'
        // insert collides. Clubs is checked first (declaration order), so its
        // Validate() failure must stop the whole operation before either
        // table's Apply() runs - including Players', whose own Validate()
        // never even gets a chance to run.
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Players.Insert((dynamic)NewPlayer(asm, 1, "Alice", 1));
                dtx.Clubs.Insert((dynamic)NewClub(asm, 2, "Arsenal Reserves", "ARS"));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);

        var playerFound = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { playerFound = ((dynamic)tx).Players.Get(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(playerFound, Is.False,
            "Players' staged insert must not survive Clubs' Validate() failure in the same operation.");
    }

    // ---- Composite index (shared Accessor across multiple fields, with Order) ----

    [Test]
    public async Task CompositeIndex_SharedAccessor_BuildsOneIndexOverBothFields() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Fixtures.Insert((dynamic)NewFixture(asm, 1, homeClubId: 10, awayClubId: 20)); return Result.Ok(); },
            PropagationMode.Optimistic);

        // Fixture's Order (see Source above) deliberately reverses
        // declaration order - AwayClubId (Order = 0) sorts before
        // HomeClubId (Order = 1), so the generated composite accessor's
        // parameter order is (awayClubId, homeClubId), not declaration
        // order. Calling it with that exact order is itself the proof Order
        // was honored - the wrong order would look up a key that was never
        // inserted and come back NotFound.
        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Fixtures.HomeAway(20, 10).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }

    [Test]
    public async Task CompositeIndex_InsertingADuplicateCombination_FailsValidation() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Fixtures.Insert((dynamic)NewFixture(asm, 1, homeClubId: 10, awayClubId: 20)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Fixtures.Insert((dynamic)NewFixture(asm, 2, homeClubId: 10, awayClubId: 20)); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public async Task CompositeIndex_SameClubsDifferentHomeAway_AreDistinctCombinations() {
        // (10, 20) and (20, 10) share both values but in opposite roles -
        // must be treated as two different composite keys, not a conflict.
        var (db, txType, asm) = NewDb();

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Fixtures.Insert((dynamic)NewFixture(asm, 1, homeClubId: 10, awayClubId: 20));
                dtx.Fixtures.Insert((dynamic)NewFixture(asm, 2, homeClubId: 20, awayClubId: 10));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public async Task CompositeIndex_DeleteThenReinsertDifferentCombination_SwapRemoveRepointsCorrectly() {
        // Exercises Table<TKey,TRow>.DeleteReturningSwap's swap-repoint path:
        // deleting the first of several rows forces a swap-remove, and the
        // composite index must still resolve correctly for the relocated row.
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Fixtures.Insert((dynamic)NewFixture(asm, 1, homeClubId: 10, awayClubId: 20));
                dtx.Fixtures.Insert((dynamic)NewFixture(asm, 2, homeClubId: 30, awayClubId: 40));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Fixtures.Delete(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Fixtures.HomeAway(40, 30).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True, "The swap-relocated row's composite index entry must still resolve at its new offset.");
    }
}
