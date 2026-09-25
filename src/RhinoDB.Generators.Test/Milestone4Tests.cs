using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// Milestone 4 originally proved secondary-index accessors were overlay-aware
// (saw this operation's own staged-but-not-yet-applied writes). That overlay
// was deliberately removed in the QuerySet/QuerySingle redesign (2026-09-18)
// - no scanning uncommitted `changes` before hitting an index, anywhere, "it will
// be the user's duty to handle it," to avoid paying that check on every search.
// This file now proves the opposite contract: a secondary-index Find/Iter during
// the same operation that staged the write does NOT see it - only a later
// operation, after Apply() has actually run, does. (The primary-key accessor's
// equivalent contract is proven in StagingSemanticsTests.cs.)
public class Milestone4Tests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

        [Table(TableKind.Instant, typeof(LeagueDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Club(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name,
            [Index(IndexKind.Hash, Uniqueness.Unique)] [property: MemoryPackOrder(2)] [property: Key(2)] string ShortCode);

        [Table(TableKind.Instant, typeof(LeagueDb))]
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
    public async Task UniqueIndex_InsertThenReadByIndexInTheSameOperation_DoesNotSeeTheStagedRow() {
        var (db, txType, asm) = NewDb();

        var foundWithinSameOperation = true;
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Club.Insert((dynamic)NewClub(asm, 1, "Arsenal", "ARS"));
                foundWithinSameOperation = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ShortCodeIsOk", (object)dtx.Club, "ARS")!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(foundWithinSameOperation, Is.False);

        var foundInALaterOperation = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                foundInALaterOperation = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ShortCodeIsOk", ((dynamic)tx).Club, "ARS")!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(foundInALaterOperation, Is.True, "Once Apply() has actually run, a later operation's Find does see it.");
    }

    [Test]
    public async Task NonUniqueIndex_InsertTwoMatchingRowsThenReadInTheSameOperation_DoesNotSeeEither() {
        var (db, txType, asm) = NewDb();

        var countWithinSameOperation = -1;
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Insert((dynamic)NewPlayer(asm, 1, "Alice", 10));
                dtx.Player.Insert((dynamic)NewPlayer(asm, 2, "Bob", 10));
                countWithinSameOperation = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ClubIdCount", (object)dtx.Player, 10)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(countWithinSameOperation, Is.EqualTo(0));

        var countInALaterOperation = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                countInALaterOperation = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "ClubIdCount", ((dynamic)tx).Player, 10)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(countInALaterOperation, Is.EqualTo(2), "Once Apply() has actually run, a later operation's Find does see both.");
    }
}
