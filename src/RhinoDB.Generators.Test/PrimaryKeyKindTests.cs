using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// Proves PrimaryKeyAttribute's new Kind parameter (defaulting to Hash) is
// actually read by TableGenerator, not just declared - an OrderedIndex-backed
// primary key round-trips the same as the default HashIndex-backed one.
public class PrimaryKeyKindTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class RankingDb : DbContext<RankingDbTransaction> {{ }}

        [Table(TableKind.Instant, typeof(RankingDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Ranking(
            [PrimaryKey(IndexKind.{0})] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name);

        // QuerySet/QuerySingle are ref structs and can never cross a dynamic call
        // boundary (see GeneratorTestHost.InvokeHelper) - typed helper instead.
        // NOTE: this fixture goes through string.Format, so every literal brace
        // below must be doubled.
        public static class TestHelpers {{
            public static bool FindIsOk(RankingDbRankingOps ranking, int id) => ranking.Primary.Find(id).HasRow();
            public static int PrimaryIterCount(RankingDbRankingOps ranking) {{ using var q = ranking.Primary.Iter(); return q.Count; }}
            public static int PrimaryRangeCount(RankingDbRankingOps ranking, int from, int to) {{ using var q = ranking.Primary.Range(from, to); return q.Count; }}
            public static int PrimaryGtCount(RankingDbRankingOps ranking, int value) {{ using var q = ranking.Primary.Gt(value); return q.Count; }}
        }}
        """;

    [Test]
    public async Task BTreePrimaryKey_InsertThenGet_RoundTrips() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(string.Format(Source, "BTree"));
        var dbType = asm.GetType("TestNs.RankingDb")!;
        var txType = asm.GetType("TestNs.RankingDbTransaction")!;
        var rankingType = asm.GetType("TestNs.Ranking")!;
        var db = Activator.CreateInstance(dbType)!;
        var ranking = Activator.CreateInstance(rankingType, 7, "Top")!;

        var result = await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Ranking.Insert((dynamic)ranking); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "FindIsOk", (object)((dynamic)tx).Ranking, 7)!; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(found, Is.True);
    }

    [Test]
    public async Task BTreePrimary_PrimaryIndex_ExposesIterAndRangeQueries() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(string.Format(Source, "BTree"));
        var dbType = asm.GetType("TestNs.RankingDb")!;
        var txType = asm.GetType("TestNs.RankingDbTransaction")!;
        var rankingType = asm.GetType("TestNs.Ranking")!;
        var db = Activator.CreateInstance(dbType)!;

        for (var id = 1; id <= 4; id++) {
            var row = Activator.CreateInstance(rankingType, id, "R" + id)!;
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Ranking.Insert((dynamic)row); return Result.Ok(); },
                PropagationMode.Optimistic);
        }

        var iterCount = 0;
        var rangeCount = 0;
        var gtCount = 0;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                var ops = (object)((dynamic)tx).Ranking;
                iterCount = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "PrimaryIterCount", ops)!;
                rangeCount = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "PrimaryRangeCount", ops, 2, 3)!;
                gtCount = (int)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "PrimaryGtCount", ops, 3)!;
                return Result.Ok();
            }, PropagationMode.Optimistic);

        Assert.That(iterCount, Is.EqualTo(4), "Primary.Iter() must reach every row, like the table-level Iter().");
        Assert.That(rangeCount, Is.EqualTo(2), "A BTree primary key must expose the ordered-index Range surface.");
        Assert.That(gtCount, Is.EqualTo(1), "Gt() is exclusive of its bound.");
    }
}
