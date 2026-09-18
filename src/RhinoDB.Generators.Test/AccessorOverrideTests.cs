using RhinoDB.Core;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Test.Generators;

// [PrimaryKey(Accessor = ...)] renames the generated Get method;
// [Table(..., Accessor = ...)] renames the generated property exposing a
// table's Ops on the database's Transaction (default "{RowTypeName}").
public class AccessorOverrideTests {
    private const string Source = """
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class LeagueDb : DbContext<LeagueDbTransaction> { }

        [Table(TableKind.Instant, typeof(LeagueDb), Accessor = "Teams")]
        public readonly partial record struct Club([PrimaryKey(Accessor = "ById")] int Id, string Name);
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
            db, txType, (ctx, tx) => { found = ((dynamic)tx).Teams.ById(1).IsOk(); return Result.Ok(); },
            PropagationMode.Optimistic);

        Assert.That(found, Is.True);
    }
}
