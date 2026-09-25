using System.Reflection;

namespace RhinoDB.Generators.Test;

// [FrozenSchema(Revision=N)] - a captured old row shape, decoded purely via the hand-rolled Raw pipeline
// (no [MemoryPackable]/[MessagePackObject] required, unlike [Table]/[CustomType] - a frozen row never
// travels over IDC/Client, only ever read back by the migration engine/genesis replay).
public class FrozenSchemaGeneratorTests {
    [Test]
    public void ValidFrozenSchema_SerializeThenDeserializeRow_RoundTrips() {
        const string source = """
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(0)]
            public readonly partial record struct Account_Rev0([PrimaryKey] int Id, decimal Balance);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var rowType = asm.GetType("TestNs.Account_Rev0")!;
        var row = Activator.CreateInstance(rowType, 42, 99.5m)!;

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.Account_Rev0FrozenSchemaOps", "SerializeRow", row)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.Account_Rev0FrozenSchemaOps", "DeserializeRow", bytes)!;

        Assert.That(roundTripped.GetType().GetProperty("Id")!.GetValue(roundTripped), Is.EqualTo(42));
        Assert.That(roundTripped.GetType().GetProperty("Balance")!.GetValue(roundTripped), Is.EqualTo(99.5m));
    }

    [Test]
    public void ValidFrozenSchema_SerializeThenDeserializeKey_RoundTrips() {
        const string source = """
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(0)]
            public readonly partial record struct Account_Rev0([PrimaryKey] int Id, decimal Balance);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.Account_Rev0FrozenSchemaOps", "SerializeKey", 42)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.Account_Rev0FrozenSchemaOps", "DeserializeKey", bytes)!;

        Assert.That(roundTripped, Is.EqualTo(42));
    }

    [Test]
    public void ValidFrozenSchema_ExposesItsOwnRevisionAsAConstant() {
        const string source = """
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(3)]
            public readonly partial record struct Account_Rev3([PrimaryKey] int Id);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.Account_Rev3FrozenSchemaOps")!;
        var revisionField = opsType.GetField("Revision", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(revisionField, Is.Not.Null);
        Assert.That(revisionField!.GetValue(null), Is.EqualTo(3));
    }

    [Test]
    public void FrozenSchemaWithNoMemoryPackableOrMessagePackObject_CompilesFine_NoMandatoryAttributeRequired() {
        // The point of this test: unlike [Table]/[CustomType] (RHINO015/RHINO017), a [FrozenSchema] type
        // must NOT require IDC/Client serialization attributes - it's Raw-only, by design.
        const string source = """
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(0)]
            public readonly partial record struct Plain_Rev0([PrimaryKey] int Id);
            """;

        Assert.DoesNotThrow(() => GeneratorTestHost.CompileAndLoad(source));
    }

    [Test]
    public void FrozenSchemaMissingPrimaryKey_ReportsRHINO022() {
        const string source = """
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(0)]
            public readonly partial record struct Account_Rev0(int Id, decimal Balance);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => GeneratorTestHost.CompileAndLoad(source));
        Assert.That(ex!.Message, Does.Contain("RHINO022"));
    }
}
