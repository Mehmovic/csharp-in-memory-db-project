using System.Reflection;

namespace RhinoDB.Test.Generators;

// [Table(...)].ChunkSize sets the generated DenseArray<TRow> storage field's
// chunk size (default 4096, rounded up to the next power of 2 by DenseArray
// itself). There's no public API exposing it - the storage field is private
// on the generated {Db} class - so these tests reach it via reflection and
// check DenseArray's own public `Capacity` (chunkCount * chunkSize),
// observable right after construction before any row is inserted.
public class ChunkSizeTests {
    private const string DefaultSource = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class DefaultChunkDb : DbContext<DefaultChunkDbTransaction> { }

        [Table(TableKind.Instant, typeof(DefaultChunkDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
        """;

    private const string ExplicitSource = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class ExplicitChunkDb : DbContext<ExplicitChunkDbTransaction> { }

        [Table(TableKind.Instant, typeof(ExplicitChunkDb), ChunkSize = 100)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
        """;

    static private int StorageCapacity(System.Reflection.Assembly asm, string dbTypeName, string fieldName) {
        var dbType = asm.GetType(dbTypeName)!;
        var db = Activator.CreateInstance(dbType)!;
        var storageField = dbType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var storage = storageField.GetValue(db)!;
        return (int)storage.GetType().GetProperty("Capacity")!.GetValue(storage)!;
    }

    [Test]
    public void ChunkSize_NotSpecified_DefaultsTo4096() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(DefaultSource);

        var capacity = StorageCapacity(asm, "TestNs.DefaultChunkDb", "widgetStorage");

        Assert.That(capacity, Is.EqualTo(4096));
    }

    [Test]
    public void ChunkSize_ExplicitValue_RoundsUpToTheNextPowerOfTwoAndBecomesTheInitialCapacity() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(ExplicitSource);

        var capacity = StorageCapacity(asm, "TestNs.ExplicitChunkDb", "widgetStorage");

        Assert.That(capacity, Is.EqualTo(128), "DenseArray rounds a 100 chunkSize up to 128 (the next power of 2).");
    }
}
