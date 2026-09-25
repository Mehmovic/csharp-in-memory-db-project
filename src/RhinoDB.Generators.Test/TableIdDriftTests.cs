using RhinoDB.Lib.Durability;

namespace RhinoDB.Generators.Test;

// The generator emits `private const uint TableId = <FNV-1a of Accessor>` (computed at compile time),
// while the runtime computes TableIdHash.Compute(accessor) from the accessor string handed to
// ColdStore.OpenTable. That is two implementations of one hash: if they ever drift apart, WAL
// entries would carry a tableId that cold storage does not recognise and every recovery would
// refuse. This pins them together by reading the emitted constant back out of the generated Ops
// type - behavioural, in the same spirit as the rest of this project suite (no source-string
// comparisons).
public class TableIdDriftTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class DriftDb : DbContext<DriftDbTransaction> { }

        [Table(TableKind.Persistent, typeof(DriftDb), Accessor = "Accounts")]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Account(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Email);
        """;

    [Test]
    public void GeneratedTableIdConst_MatchesTheRuntimeHash() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);

        var opsType = asm.GetType("TestNs.DriftDbAccountsOps");
        Assert.That(opsType, Is.Not.Null, "The generated Ops type must exist under the expected name.");

        var field = opsType!.GetField("TableId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.That(field, Is.Not.Null, "The generated Ops type must carry the private const TableId.");

        var emitted = (uint)field!.GetRawConstantValue()!;
        Assert.That(emitted, Is.EqualTo(TableIdHash.Compute("Accounts")),
            "The generated TableId const and TableIdHash.Compute must agree, or the WAL and cold storage would disagree about which table a change belongs to.");
    }
}