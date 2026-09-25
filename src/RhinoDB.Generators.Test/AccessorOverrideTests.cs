using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// [PrimaryKey(Accessor = ...)] renames the generated Get method;
// [Table(..., Accessor = ...)] renames the generated property exposing a
// table's Ops on the database's Transaction (default "{RowTypeName}").
public class AccessorOverrideTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

        [Table(TableKind.Instant, typeof(LeagueDb), Accessor = "Teams")]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Club(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name);

        // QuerySet/QuerySingle are ref structs and can never cross a dynamic call
        // boundary (see GeneratorTestHost.InvokeHelper) - typed helper instead.
        public static class TestHelpers {
            public static bool PrimaryFindIsOk(LeagueDbTeamsOps teams, int id) => teams.Primary.Find(id).HasRow();
        }
        """;

    static private (object Db, Type TxType, System.Reflection.Assembly Assembly) NewDb() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.LeagueDb")!;
        var txType = asm.GetType("TestNs.LeagueDbTransaction")!;
        var db = Activator.CreateInstance(dbType)!;
        return (db, txType, asm);
    }

    static private object NewClub(System.Reflection.Assembly assembly, int id, string name) {
        var t = assembly.GetType("TestNs.Club")!;
        return Activator.CreateInstance(t, id, name)!;
    }

    [Test]
    public async Task TableAccessor_RenamesTheTransactionProperty() {
        var (db, txType, asm) = NewDb();

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Teams.Insert((dynamic)NewClub(asm, 1, "Arsenal")); return Result.Ok(); },
            PropagationMode.Optimistic);

        var found = false;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { found = (bool)GeneratorTestHost.InvokeHelper(asm, "TestNs.TestHelpers", "PrimaryFindIsOk", (object)((dynamic)tx).Teams, 1)!; return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }
}
