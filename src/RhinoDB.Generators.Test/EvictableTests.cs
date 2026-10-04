namespace RhinoDB.Generators.Test;

// [Table].Evictable (default false, Persistent-kind only) - false means
// .Storage isn't generated on that table's Ops class at all (compile-time
// absence, not a runtime guard), since a non-evictable table is expected to
// be eager-loaded in full by the generated database loader and never
// partially evicted. true generates .Storage as usual (Milestone3Tests.cs/
// PersistentSecondaryIndexTests.cs/PersistentDurabilityTests.cs already
// cover that behavior end to end).
public class EvictableTests {
    [Test]
    public void EvictableNotSet_PersistentTable_DoesNotExposeStorage() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [Table<VaultDb>(TableKind.Persistent)]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;

        Assert.That(opsType.GetProperty("Storage"), Is.Null, "A non-evictable table's Ops class must not declare a Storage property at all.");
    }

    [Test]
    public void EvictableFalseExplicitly_PersistentTable_DoesNotExposeStorage() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [Table<VaultDb>(TableKind.Persistent, Evictable = false)]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;

        Assert.That(opsType.GetProperty("Storage"), Is.Null);
    }

    [Test]
    public void EvictableTrue_PersistentTable_ExposesStorage() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class VaultDb : DbContext<VaultDbTransaction> { }

            [Table<VaultDb>(TableKind.Persistent, Evictable = true)]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Account(
                [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
                [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.VaultDbAccountOps")!;

        Assert.That(opsType.GetProperty("Storage"), Is.Not.Null);
    }

    [Test]
    public void EvictableTrue_OnAnInstantKindTable_ReportsRHINO008() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class ShopDb : DbContext<ShopDbTransaction> { }

            [Table<ShopDb>(TableKind.Instant, Evictable = true)]
            [MemoryPackable(GenerateType.VersionTolerant)]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO008"));
    }
}
