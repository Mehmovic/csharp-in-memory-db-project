namespace RhinoDB.Test.Generators;

public class InvalidGenerationsTests {
    [Test]
    public void InvalidGenerationsContainsZero_ReportsRHINO021() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database(InvalidGenerations = new[] { 0 })]
            public partial class ZeroGenDb : DbContext<ZeroGenDbTransaction> { }

            [Table(TableKind.Instant, typeof(ZeroGenDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO021"));
    }

    [Test]
    public void InvalidGenerationsContainsANegativeNumber_ReportsRHINO021() {
        const string source = """
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database(InvalidGenerations = new[] { -1 })]
            public partial class NegativeGenDb : DbContext<NegativeGenDbTransaction> { }

            [Table(TableKind.Instant, typeof(NegativeGenDb))]
            public readonly partial record struct Widget([PrimaryKey] int Id);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO021"));
    }

    [Test]
    public void NoInvalidGenerationsDeclared_DoesNotThrow_IsGenerationInvalidAlwaysFalse() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database]
            public partial class PlainDb : DbContext<PlainDbTransaction> { }

            [Table(TableKind.Instant, typeof(PlainDb))]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var dbType = asm.GetType("TestNs.PlainDb")!;
        var isInvalid = dbType.GetMethod("IsGenerationInvalid")!;

        Assert.That(isInvalid.Invoke(null, [0]), Is.False);
        Assert.That(isInvalid.Invoke(null, [7]), Is.False);
    }

    [Test]
    public void PositiveInvalidGenerationsDeclared_IsGenerationInvalidReflectsThemOnly() {
        const string source = """
            using MemoryPack;
            using MessagePack;
            using RhinoDB.Core.Tables;
            using RhinoDB.Lib.Execution;

            namespace TestNs;

            [Database(InvalidGenerations = new[] { 3, 5 })]
            public partial class TaintedDb : DbContext<TaintedDbTransaction> { }

            [Table(TableKind.Instant, typeof(TaintedDb))]
            [MemoryPackable]
            [MessagePackObject]
            public readonly partial record struct Widget([PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var dbType = asm.GetType("TestNs.TaintedDb")!;
        var isInvalid = dbType.GetMethod("IsGenerationInvalid")!;

        Assert.That(isInvalid.Invoke(null, [3]), Is.True);
        Assert.That(isInvalid.Invoke(null, [5]), Is.True);
        Assert.That(isInvalid.Invoke(null, [1]), Is.False);
        Assert.That(isInvalid.Invoke(null, [4]), Is.False);
    }
}
