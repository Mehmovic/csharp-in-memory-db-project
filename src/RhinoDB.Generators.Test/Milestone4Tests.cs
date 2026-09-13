using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Milestone 4: secondary-index accessors become overlay-aware - they now see
// this operation's own staged-but-not-yet-applied writes, the same
// read-your-own-writes guarantee the primary Get() accessor already had.
// Before this milestone, every test below that reads a secondary index in
// the SAME operation that staged the write would have failed (or, worse,
// returned a stale real-storage hit) - see Milestone2Tests.cs's
// UniqueSecondaryIndex_ShortCode_FindsTheInsertedRow, which used two
// separate operations specifically to route around this gap.
public class Milestone4Tests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

        [Table(TableKind.Instant, typeof(LeagueDb))]
        public readonly partial record struct Club(
            [PrimaryKey] int Id,
            string Name,
            [Index(IndexKind.Hash, Uniqueness.Unique)] string ShortCode);

        [Table(TableKind.Instant, typeof(LeagueDb))]
        public readonly partial record struct Player(
            [PrimaryKey] int Id,
            string Name,
            [Index(IndexKind.Hash, Uniqueness.NonUnique)] int ClubId);
        """;

    static private (object Db, Type TxType, System.Reflection.Assembly Assembly) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.LeagueDb")!;
        var txType = asm.GetType("TestNs.LeagueDbTransaction")!;
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

    // ---- Unique index overlay ----

    [Test]
    public async Task UniqueIndex_InsertThenReadByIndexInTheSameOperation_FindsTheStagedRow() {
        var (db, txType, asm) = NewDb();

        var found = false;
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS"));
                found = dtx.Club.ShortCode("ARS").IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(found, Is.True);
    }

    [Test]
    public async Task UniqueIndex_UpdateChangingTheIndexedField_MakesTheOldValueUnresolvableInTheSameOperation() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        bool oldFound = true, newFound = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Club.Update(1, (dynamic)NewClub(asm, 1, "Arsenal", "GUN"));
                // The real index still says "ARS" -> offset 0, but this
                // operation's own staged Update means that's now stale -
                // must not be trusted just because Apply() hasn't run yet.
                oldFound = dtx.Club.ShortCode("ARS").IsOk();
                newFound = dtx.Club.ShortCode("GUN").IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(oldFound, Is.False, "A real-storage hit whose row was touched by this batch must not be trusted once stale.");
        Assert.That(newFound, Is.True, "The new value must be visible via the overlay before Apply() has run.");
    }

    [Test]
    public async Task UniqueIndex_DeleteInTheSameOperation_MakesTheRowUnresolvableByIndex() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Club.Delete(1);
                found = dtx.Club.ShortCode("ARS").IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(found, Is.False);
    }

    // ---- Non-unique index overlay ----

    [Test]
    public async Task NonUniqueIndex_InsertTwoMatchingRowsThenReadInTheSameOperation_FindsBoth() {
        var (db, txType, asm) = NewDb();

        var count = -1;
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10));
                dtx.Player.Insert((dynamic)NewPlayer(asm, 2, "Bob", 10));
                count = dtx.Player.ClubId(10).Count;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task NonUniqueIndex_UpdatingOneRowAwayFromTheGroup_ExcludesItButKeepsTheOthers() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10));
                dtx.Player.Insert((dynamic)NewPlayer(asm, 2, "Bob", 10));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        int oldGroupCount = -1, newGroupCount = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Update(1, (dynamic)NewPlayer(asm, 1, "Alice", 99));
                oldGroupCount = dtx.Player.ClubId(10).Count;
                newGroupCount = dtx.Player.ClubId(99).Count;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(oldGroupCount, Is.EqualTo(1), "Only Bob should remain in club 10 - Alice's real-storage entry is stale within this batch.");
        Assert.That(newGroupCount, Is.EqualTo(1), "Alice's updated row must be visible via the overlay before Apply() has run.");
    }
}
