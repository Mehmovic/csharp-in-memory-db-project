using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

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
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

        [Table<LeagueDb>(TableKind.Instant)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Club(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name,
            [Index(IndexKind.Hash, Uniqueness.Unique)] [property: MemoryPackOrder(2)] [property: Key(2)] string ShortCode);

        [Table<LeagueDb>(TableKind.Instant)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Player(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name,
            [Index(IndexKind.Hash, Uniqueness.NonUnique)] [property: MemoryPackOrder(2)] [property: Key(2)] int ClubId);

        // QuerySet/QuerySingle are ref structs and can never cross a dynamic call boundary
        // (see GeneratorTestHost.InvokeHelper) - these small helpers do the Idx.X.Find(...) touching
        // as real static-typed C#, exposing only reflection-safe (non-ref-struct) signatures.
        public static class TestHelpers {
            public static bool ShortCodeIsOk(LeagueDbClubOps club, string code) => club.Idx.ShortCode.Find(code).Get().IsOk();
            public static int ClubIdCount(LeagueDbPlayerOps player, int clubId) { using var r = player.Idx.ClubId.Find(clubId); return r.Count; }
        }
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
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Club.Update(1, (dynamic)NewClub(asm, 1, "Arsenal FC", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);

        // Separate operation - hits the real index, not the overlay, so this
        // proves Apply() left ShortCode's mapping in place rather than
        // needing a redundant Delete+Insert to keep it correct.
        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ShortCodeIsOk", ((dynamic)tx).Instant.Club, "ARS")!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }

    [Test]
    public async Task UniqueIndex_ChangingAFieldAwayThenBackToItsOriginalValue_DoesNotFalselyCollideWithAStaleEntry() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Club.Update(1, (dynamic)NewClub(asm, 1, "Arsenal", "GUN")); return Result.Ok(); },
            PropagationMode.Optimistic);

        // Revert - if the away-move ever left a stale "ARS" -> offset entry
        // behind (the historical bug this optimization must avoid), this
        // would either fail as a false duplicate or leave two entries.
        var revert = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Club.Update(1, (dynamic)NewClub(asm, 1, "Arsenal", "ARS")); return Result.Ok(); },
            PropagationMode.Optimistic);
        Assert.That(revert.IsOk(), Is.True);

        bool arsFound = false, gunFound = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                arsFound = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ShortCodeIsOk", ((dynamic)tx).Instant.Club, "ARS")!;
                gunFound = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ShortCodeIsOk", ((dynamic)tx).Instant.Club, "GUN")!;
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
                dtx.Instant.Player.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10));
                dtx.Instant.Player.Insert((dynamic)NewPlayer(asm, 2, "Bob", 10));
                return Result.Ok();
            }, PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Player.Update(1, (dynamic)NewPlayer(asm, 1, "Alicia", 10)); return Result.Ok(); },
            PropagationMode.Optimistic);

        var count = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                count = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ClubIdCount", ((dynamic)tx).Instant.Player, 10)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(count, Is.EqualTo(2), "Both players must still resolve under club 10 after an unrelated-field update.");
    }

    [Test]
    public async Task NonUniqueIndex_MovingARowToADifferentGroupThenBack_LeavesNoStaleEntryInTheOldGroup() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Player.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10)); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Player.Update(1, (dynamic)NewPlayer(asm, 1, "Alice", 20)); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Instant.Player.Update(1, (dynamic)NewPlayer(asm, 1, "Alice", 10)); return Result.Ok(); },
            PropagationMode.Optimistic);

        int club10Count = -1, club20Count = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                club10Count = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ClubIdCount", ((dynamic)tx).Instant.Player, 10)!;
                club20Count = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ClubIdCount", ((dynamic)tx).Instant.Player, 20)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(club10Count, Is.EqualTo(1), "The reverted group must contain exactly the one row, not a duplicate.");
        Assert.That(club20Count, Is.EqualTo(0), "The abandoned intermediate group must have no stale entry left behind.");
    }
}
