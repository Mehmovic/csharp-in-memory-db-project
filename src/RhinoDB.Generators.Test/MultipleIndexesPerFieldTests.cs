using System.Collections.Immutable;
using System.Reflection;
using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// One field may carry more than one [Index] attribute (IndexAttribute allows
// multiple), so a single row field can feed several independent indexes, each with
// its own Accessor, kind and uniqueness. The generator must therefore resolve ONE
// INDEX PER ATTRIBUTE - not one per field - and give each one its own emitted index
// field, otherwise a second index on the same field would silently vanish (or
// collide with the first).
public class MultipleIndexesPerFieldTests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

        [Table(TableKind.Instant, typeof(LeagueDb))]
        public readonly partial record struct Player(
            [PrimaryKey] int Id,
            [Index(IndexKind.Hash, Uniqueness.NonUnique, Accessor = "ByClub")]
            [Index(IndexKind.BTree, Uniqueness.NonUnique, Accessor = "ByClubOrdered")]
            int ClubId,
            [Index(IndexKind.Hash, Uniqueness.Unique, Accessor = "ByShirt")]
            [Index(IndexKind.BTree, Uniqueness.NonUnique, Accessor = "ByShirtLoose")]
            int ShirtNumber,
            string Name);
        """;

    static private (object Db, Type TxType, Assembly Assembly) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.LeagueDb")!;
        var txType = asm.GetType("TestNs.LeagueDbTransaction")!;
        var db = Activator.CreateInstance(dbType)!;
        return (db, txType, asm);
    }

    static private object NewPlayer(Assembly assembly, int id, int clubId, int shirtNumber, string name) {
        var t = assembly.GetType("TestNs.Player")!;
        return Activator.CreateInstance(t, id, clubId, shirtNumber, name)!;
    }

    [Test]
    public async Task TwoIndexesOnOneField_BothResolveAfterInsert() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Insert((dynamic)NewPlayer(asm, 1, 1, 7, "Alpha"));
                dtx.Player.Insert((dynamic)NewPlayer(asm, 2, 1, 8, "Beta"));
                return Result.Ok();
            }, PropagationMode.Optimistic);

        var byClub = -1;
        var byClubOrdered = -1;
        var byShirtResolves = false;
        var byShirtLoose = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                byClub = dtx.Player.ByClub(1).Count;
                byClubOrdered = dtx.Player.ByClubOrdered(1).Count;
                byShirtResolves = dtx.Player.ByShirt(7).IsOk();
                byShirtLoose = dtx.Player.ByShirtLoose(7).Count;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(byClub, Is.EqualTo(2));
        Assert.That(byClubOrdered, Is.EqualTo(2));
        Assert.That(byShirtResolves, Is.True);
        Assert.That(byShirtLoose, Is.EqualTo(1));
    }

    [Test]
    public async Task TwoIndexesOnOneField_BothFollowAnUpdate() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)NewPlayer(asm, 1, 1, 7, "Alpha")); return Result.Ok(); },
            PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Update(1, (dynamic)NewPlayer(asm, 1, 2, 7, "Alpha")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var oldClub = -1;
        var oldClubOrdered = -1;
        var newClub = -1;
        var newClubOrdered = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                oldClub = dtx.Player.ByClub(1).Count;
                oldClubOrdered = dtx.Player.ByClubOrdered(1).Count;
                newClub = dtx.Player.ByClub(2).Count;
                newClubOrdered = dtx.Player.ByClubOrdered(2).Count;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(oldClub, Is.EqualTo(0));
        Assert.That(oldClubOrdered, Is.EqualTo(0));
        Assert.That(newClub, Is.EqualTo(1));
        Assert.That(newClubOrdered, Is.EqualTo(1));
    }

    [Test]
    public async Task TwoIndexesOnOneField_BothFollowADelete() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Player.Insert((dynamic)NewPlayer(asm, 1, 1, 7, "Alpha"));
                dtx.Player.Insert((dynamic)NewPlayer(asm, 2, 1, 8, "Beta"));
                return Result.Ok();
            }, PropagationMode.Optimistic);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Delete(1); return Result.Ok(); },
            PropagationMode.Optimistic);

        var byClub = -1;
        var byClubOrdered = -1;
        var byShirtResolves = true;
        var byShirtLoose = -1;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                byClub = dtx.Player.ByClub(1).Count;
                byClubOrdered = dtx.Player.ByClubOrdered(1).Count;
                byShirtResolves = dtx.Player.ByShirt(7).IsOk();
                byShirtLoose = dtx.Player.ByShirtLoose(7).Count;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(byClub, Is.EqualTo(1));
        Assert.That(byClubOrdered, Is.EqualTo(1));
        Assert.That(byShirtResolves, Is.False);
        Assert.That(byShirtLoose, Is.EqualTo(0));
    }

    [Test]
    public async Task TheUniqueSiblingStillRejectsDuplicates_TheNonUniqueOneWouldAllow() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)NewPlayer(asm, 1, 1, 7, "Alpha")); return Result.Ok(); },
            PropagationMode.Optimistic);

        // Same ShirtNumber, different player: ByShirt is Unique and must reject it,
        // even though the sibling ByShirtLoose index on the same field would allow it.
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Player.Insert((dynamic)NewPlayer(asm, 2, 2, 7, "Beta")); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.DuplicateKey));
    }

    [Test]
    public void BothIndexesGetTheirOwnIndexField_SoAccessorNamesCannotCollide() {
        var (db, txType, asm) = NewDb();
        _ = db;
        _ = txType;

        var fieldNames = asm.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(f => f.Name))
            .ToImmutableArray();

        // One emitted index field per [Index] attribute - four attributes on two
        // fields, four distinctly named index fields. Names are matched by suffix,
        // since different generated types prefix them with the table accessor.
        bool HasFieldEndingWith(string suffix) => fieldNames.Any(n => n.EndsWith(suffix, StringComparison.Ordinal));

        Assert.That(HasFieldEndingWith("ByClubIndex"), Is.True, "ByClub needs its own index field");
        Assert.That(HasFieldEndingWith("ByClubOrderedIndex"), Is.True, "the second index on ClubId needs its own index field");
        Assert.That(HasFieldEndingWith("ByShirtIndex"), Is.True, "ByShirt needs its own index field");
        Assert.That(HasFieldEndingWith("ByShirtLooseIndex"), Is.True, "the second index on ShirtNumber needs its own index field");
    }
}
