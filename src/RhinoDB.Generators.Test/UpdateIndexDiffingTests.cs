using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Apply()'s Update case only touches an index whose own field(s) actually
// changed (compares oldRow's key expression against the new row's via
// .Equals(), skipping the Delete+Insert pair when equal) - added 2026-09-12
// as a requested optimization, since updating one field previously paid the
// cost of re-registering every OTHER unrelated index too. Docs/03-roadmap.md
// Stage 3 notes an early, buggy attempt at this same optimization apparently
// skipped re-registration under the wrong condition and left a stale index
// entry behind - a real self-collision risk (reverting a field back to a
// value it held earlier could then collide with its own uncleaned stale
// entry). This suite specifically stresses that risk: unrelated-field
// updates must leave an index's own mapping intact and still resolvable,
// and changing a field away and back must never leave two stale mappings
// behind or falsely reject the revert as a duplicate.
public class UpdateIndexDiffingTests {
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

    [Test]
    public async Task UniqueIndex_UpdatingAnUnrelatedField_LeavesTheMappingIntactAndStillResolvable() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Update(1, (dynamic)NewClub(asm, 1, "Arsenal FC", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        // Separate operation - hits the real index, not the overlay, so this
        // proves Apply() left ShortCode's mapping in place rather than
        // needing a redundant Delete+Insert to keep it correct.
        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Club.ShortCode("ARS").IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }

    [Test]
    public async Task UniqueIndex_ChangingAFieldAwayThenBackToItsOriginalValue_DoesNotFalselyCollideWithAStaleEntry() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Update(1, (dynamic)NewClub(asm, 1, "Arsenal", "GUN")); return Result.Ok(); },
            PropagationMode.Optimistic);

        // Revert - if the away-move ever left a stale "ARS" -> offset entry
        // behind (the historical bug this optimization must avoid), this
        // would either fail as a false duplicate or leave two entries.
        var revert = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Club.Update(1, (dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);
        Assert.That(revert.IsOk(), Is.True);

        bool arsFound = false, gunFound = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                arsFound = dtx.Club.ShortCode("ARS").IsOk();
                gunFound = dtx.Club.ShortCode("GUN").IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(arsFound, Is.True, "The reverted value must resolve.");
        Assert.That(gunFound, Is.False, "The abandoned intermediate value must not leave a stale entry behind.");
    }

    [Test]
    public async Task NonUniqueIndex_UpdatingAnUnrelatedField_LeavesTheRowFindableInItsGroup() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10));
                dtx.Player.Insert((dynamic)NewPlayer(asm, 2, "Bob", 10));
                return Result.Ok();
            }, PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Update(1, (dynamic)NewPlayer(asm, 1, "Alicia", 10)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var count = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { count = ((dynamic)tx).Player.ClubId(10).Count; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(count, Is.EqualTo(2), "Both players must still resolve under club 10 after an unrelated-field update.");
    }

    [Test]
    public async Task NonUniqueIndex_MovingARowToADifferentGroupThenBack_LeavesNoStaleEntryInTheOldGroup() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10)); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Update(1, (dynamic)NewPlayer(asm, 1, "Alice", 20)); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Update(1, (dynamic)NewPlayer(asm, 1, "Alice", 10)); return Result.Ok(); },
            PropagationMode.Optimistic);

        int club10Count = -1, club20Count = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                club10Count = dtx.Player.ClubId(10).Count;
                club20Count = dtx.Player.ClubId(20).Count;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(club10Count, Is.EqualTo(1), "The reverted group must contain exactly the one row, not a duplicate.");
        Assert.That(club20Count, Is.EqualTo(0), "The abandoned intermediate group must have no stale entry left behind.");
    }
}
