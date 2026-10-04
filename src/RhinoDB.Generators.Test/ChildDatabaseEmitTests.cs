namespace RhinoDB.Generators.Test;

// [ChildDatabase<TRoot,TKey>] needs the exact same EmitDatabase codegen a Root [Database] gets (its
// own generated {ChildDb}Transaction, storage/ColdTable wiring) but must NEVER receive a [Table]'s
// omitted-database broadcast - a Child only ever gets tables explicitly declared [Table<TheChildDb>]
// for it. Confirmed with the user, not assumed - these tests pin that distinction down.
public class ChildDatabaseEmitTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class RootDb : DbContext<RootDbTransaction> { }

        [ChildDatabase<RootDb, string>]
        public partial class ChildDb : DbContext<ChildDbTransaction> { }

        [Table(TableKind.Instant)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct BroadcastRow(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name);

        [Table<ChildDb>(TableKind.Persistent)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct ChildOnlyRow(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Name);
        """;

    [Test]
    public void ChildDatabase_GetsItsOwnGeneratedTransactionType() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

        Assert.That(asm.GetType("TestNs.ChildDbTransaction"), Is.Not.Null);
    }

    [Test]
    public void ChildDatabase_GetsTablesExplicitlyDeclaredForIt() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

        Assert.That(asm.GetType("TestNs.ChildDbChildOnlyRowOps"), Is.Not.Null);
        var transactionType = asm.GetType("TestNs.ChildDbTransaction")!;
        Assert.That(transactionType.GetProperty("ChildOnlyRow"), Is.Not.Null, "the child's own transaction must expose its explicitly-declared table.");
    }

    [Test]
    public void ChildDatabase_NeverReceivesAnOmittedDatabaseTablesBroadcast() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

        Assert.That(asm.GetType("TestNs.ChildDbBroadcastRowOps"), Is.Null, "[Table] with an omitted database must broadcast only to Root [Database] types, never to Children.");
    }

    [Test]
    public void RootDatabase_StillReceivesTheOmittedDatabaseTablesBroadcast_Unaffected() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

        Assert.That(asm.GetType("TestNs.RootDbBroadcastRowOps"), Is.Not.Null, "sanity check - the pre-existing Root broadcast behavior must be unchanged.");
    }
}
