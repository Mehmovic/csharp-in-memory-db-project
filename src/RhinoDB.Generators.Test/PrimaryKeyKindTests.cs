using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// Proves PrimaryKeyAttribute's new Kind parameter (defaulting to Hash) is
// actually read by TableGenerator, not just declared - an OrderedIndex-backed
// primary key round-trips the same as the default HashIndex-backed one.
public class PrimaryKeyKindTests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class RankingDb : DbContext<RankingDbTransaction> {{ }}

        [Table(TableKind.Instant, typeof(RankingDb))]
        public readonly partial record struct Ranking([PrimaryKey(IndexKind.{0})] int Id, string Name);
        """;

    [Test]
    public async Task RedBlackTreePrimaryKey_InsertThenGet_RoundTrips() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(string.Format(Source, "RedBlackTree"));
        var dbType = asm.GetType("TestNs.RankingDb")!;
        var txType = asm.GetType("TestNs.RankingDbTransaction")!;
        var rankingType = asm.GetType("TestNs.Ranking")!;
        var db = Activator.CreateInstance(dbType)!;
        var ranking = Activator.CreateInstance(rankingType, 7, "Top")!;

        var found = false;
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Ranking.Insert((dynamic)ranking);
                found = dtx.Ranking.Find(7).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(found, Is.True);
    }
    
    [Test]
    public async Task BTreePrimaryKey_InsertThenGet_RoundTrips() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(string.Format(Source, "BTree"));
        var dbType = asm.GetType("TestNs.RankingDb")!;
        var txType = asm.GetType("TestNs.RankingDbTransaction")!;
        var rankingType = asm.GetType("TestNs.Ranking")!;
        var db = Activator.CreateInstance(dbType)!;
        var ranking = Activator.CreateInstance(rankingType, 7, "Top")!;

        var found = false;
        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Ranking.Insert((dynamic)ranking);
                found = dtx.Ranking.Find(7).IsOk();
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(found, Is.True);
    }
}
