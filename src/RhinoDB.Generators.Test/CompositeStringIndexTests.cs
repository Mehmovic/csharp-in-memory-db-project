using System.Reflection;
using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Composite indexes whose second column is a string. A composite key is just TKey
// being a value tuple, but a string element adds: reference-type equality on the
// lookup side, and real ordering work for the ordered kinds. The existing composite
// suite (Milestone2Tests) only covers (int, int) with a Hash index, so this file
// adds the string variant plus the BTree kind - the chunk-based index, where the
// composite key has to survive chunk splits, binary-search starts and swap-removes.
public class CompositeStringIndexTests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

        [Table(TableKind.Instant, typeof(LeagueDb))]
        public readonly partial record struct Player(
            [PrimaryKey] int Id,
            [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "ByClubAndName", Order = 0)] int ClubId,
            [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "ByClubAndName", Order = 1)] string Name);

        [Table(TableKind.Instant, typeof(LeagueDb))]
        public readonly partial record struct SquadSlot(
            [PrimaryKey] int Id,
            [Index(IndexKind.BTree, Uniqueness.NonUnique, Accessor = "ByClubAndRole", Order = 0)] int ClubId,
            [Index(IndexKind.BTree, Uniqueness.NonUnique, Accessor = "ByClubAndRole", Order = 1)] string Role);
        """;

    static private (object Db, Type TxType, Assembly Assembly) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.LeagueDb")!;
        var txType = asm.GetType("TestNs.LeagueDbTransaction")!;
        var db = Activator.CreateInstance(dbType)!;
        return (db, txType, asm);
    }

    static private object NewPlayer(Assembly assembly, int id, int clubId, string name) {
        var t = assembly.GetType("TestNs.Player")!;
        return Activator.CreateInstance(t, id, clubId, name)!;
    }

    static private object NewSlot(Assembly assembly, int id, int clubId, string role) {
        var t = assembly.GetType("TestNs.SquadSlot")!;
        return Activator.CreateInstance(t, id, clubId, role)!;
    }

    [Test]
    public async Task UniqueCompositeIntString_LookupResolvesTheExactPair() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Insert((dynamic)NewPlayer(asm, 1, 10, "alice"));
                dtx.Player.Insert((dynamic)NewPlayer(asm, 2, 10, "bob"));
                dtx.Player.Insert((dynamic)NewPlayer(asm, 3, 20, "alice"));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var aliceInTen = false;
        var aliceInTwenty = false;
        var partialMatch = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                aliceInTen = dtx.Player.ByClubAndName(10, "alice").IsOk();
                aliceInTwenty = dtx.Player.ByClubAndName(20, "alice").IsOk();
                // Same club, name that was never inserted - the composite key must
                // not degrade into a club-only match.
                partialMatch = dtx.Player.ByClubAndName(10, "carol").IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(aliceInTen, Is.True);
        Assert.That(aliceInTwenty, Is.True);
        Assert.That(partialMatch, Is.False);
    }

    [Test]
    public async Task UniqueCompositeIntString_DuplicateCombination_FailsValidation() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)NewPlayer(asm, 1, 10, "alice")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)NewPlayer(asm, 2, 10, "alice")); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public async Task UniqueCompositeIntString_SameClubDifferentName_BothAccepted() {
        var (db, txType, asm) = NewDb();

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Insert((dynamic)NewPlayer(asm, 1, 10, "alice"));
                dtx.Player.Insert((dynamic)NewPlayer(asm, 2, 10, "bob"));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
    }

    [Test]
    public async Task UniqueCompositeIntString_UpdateMaintainsTheIndex() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)NewPlayer(asm, 1, 10, "alice")); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Update(1, (dynamic)NewPlayer(asm, 1, 10, "alicia")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var oldPair = true;
        var newPair = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                oldPair = dtx.Player.ByClubAndName(10, "alice").IsOk();
                newPair = dtx.Player.ByClubAndName(10, "alicia").IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(oldPair, Is.False);
        Assert.That(newPair, Is.True);
    }

    [Test]
    public async Task UniqueCompositeIntString_DeleteMakesThePairUnresolvable() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)NewPlayer(asm, 1, 10, "alice")); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Delete(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = true;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Player.ByClubAndName(10, "alice").IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.False);
    }

    [Test]
    public async Task NonUniqueCompositeIntStringBTree_ReturnsOnlyTheMatchingPairs() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.SquadSlot.Insert((dynamic)NewSlot(asm, 1, 10, "gk"));
                dtx.SquadSlot.Insert((dynamic)NewSlot(asm, 2, 10, "gk")); // same pair - allowed, not unique
                dtx.SquadSlot.Insert((dynamic)NewSlot(asm, 3, 10, "cb"));
                dtx.SquadSlot.Insert((dynamic)NewSlot(asm, 4, 20, "gk")); // other club
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var gkInTen = -1;
        var cbInTen = -1;
        var gkInTwenty = -1;
        var unmatched = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                gkInTen = dtx.SquadSlot.ByClubAndRole(10, "gk").Count;
                cbInTen = dtx.SquadSlot.ByClubAndRole(10, "cb").Count;
                gkInTwenty = dtx.SquadSlot.ByClubAndRole(20, "gk").Count;
                unmatched = dtx.SquadSlot.ByClubAndRole(10, "st").Count;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(gkInTen, Is.EqualTo(2));
        Assert.That(cbInTen, Is.EqualTo(1));
        Assert.That(gkInTwenty, Is.EqualTo(1));
        Assert.That(unmatched, Is.EqualTo(0));
    }

    [Test]
    public async Task NonUniqueCompositeIntStringBTree_SurvivesASwapRemoveOnDelete() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.SquadSlot.Insert((dynamic)NewSlot(asm, 1, 10, "gk"));
                dtx.SquadSlot.Insert((dynamic)NewSlot(asm, 2, 20, "cb"));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).SquadSlot.Delete(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        // Deleting the first row forces a swap-remove that relocates the second one -
        // its composite entry must still resolve at the new offset.
        var relocated = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { relocated = ((dynamic)tx).SquadSlot.ByClubAndRole(20, "cb").Count; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(relocated, Is.EqualTo(1));
    }
}
