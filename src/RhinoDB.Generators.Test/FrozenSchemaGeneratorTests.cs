using System.Reflection;

using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// [FrozenSchema(Revision=N)] - a captured old row shape, decoded via the hand-rolled Raw pipeline always
// (no [MemoryPackable]/[MessagePackObject] required under the default Raw protocol, unlike [Table]/
// [CustomType]). Since Part F, Phase 2: when the project's ClientProtocol is VersionedMemoryPack or
// MessagePack, a frozen type ALSO needs the matching mandatory attribute + gets a matching
// Serialize{Format}/Deserialize{Format} wrapper pair - CompatAdapter's upgrade path needs to decode an old
// client's bytes in the project's own client wire format, not just Raw.
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
        var revisionField = opsType.GetField("Revision", BindingFlags.Public | BindingFlags.Static);

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

    [Test]
    public void VersionedMemoryPackProtocol_FrozenSchemaMissingMemoryPackable_ReportsRHINO025() {
        const string source = """
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(0)]
            public readonly partial record struct Account_Rev0([PrimaryKey] int Id, decimal Balance);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GeneratorTestHost.CompileAndLoadWithClientProtocol(source, ClientProtocolKind.VersionedMemoryPack));
        Assert.That(ex!.Message, Does.Contain("RHINO025"));
        Assert.That(ex.Message, Does.Contain("[MemoryPackable]"));
    }

    [Test]
    public void MessagePackProtocol_FrozenSchemaMissingMessagePackObject_ReportsRHINO025() {
        const string source = """
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(0)]
            public readonly partial record struct Account_Rev0([PrimaryKey] int Id, decimal Balance);
            """;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GeneratorTestHost.CompileAndLoadWithClientProtocol(source, ClientProtocolKind.MessagePack));
        Assert.That(ex!.Message, Does.Contain("RHINO025"));
        Assert.That(ex.Message, Does.Contain("[MessagePackObject]"));
    }

    [Test]
    public void VersionedMemoryPackProtocol_FrozenSchemaWithMemoryPackable_GetsMatchingWrapperPair() {
        const string source = """
            using MemoryPack;
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(0)]
            [MemoryPackable]
            public readonly partial record struct Account_Rev0([PrimaryKey] int Id, decimal Balance);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoadWithClientProtocol(source, ClientProtocolKind.VersionedMemoryPack);
        var opsType = asm.GetType("TestNs.Account_Rev0FrozenSchemaOps")!;

        Assert.That(opsType.GetMethod("SerializeVersionedMemoryPack", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
        Assert.That(opsType.GetMethod("DeserializeVersionedMemoryPack", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
        Assert.That(opsType.GetMethod("SerializeMessagePack", BindingFlags.Public | BindingFlags.Static), Is.Null);
    }

    [Test]
    public void MessagePackProtocol_FrozenSchemaWithMessagePackObject_GetsMatchingWrapperPair() {
        const string source = """
            using MessagePack;
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(0)]
            [MessagePackObject]
            public readonly partial record struct Account_Rev0([PrimaryKey] [property: Key(0)] int Id, [property: Key(1)] decimal Balance);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoadWithClientProtocol(source, ClientProtocolKind.MessagePack);
        var opsType = asm.GetType("TestNs.Account_Rev0FrozenSchemaOps")!;

        Assert.That(opsType.GetMethod("SerializeMessagePack", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
        Assert.That(opsType.GetMethod("DeserializeMessagePack", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
        Assert.That(opsType.GetMethod("SerializeVersionedMemoryPack", BindingFlags.Public | BindingFlags.Static), Is.Null);
    }

    [Test]
    public void RawProtocol_FrozenSchemaGetsNeitherWrapperPair() {
        const string source = """
            using RhinoDB.Core.Tables;

            namespace TestNs;

            [FrozenSchema(0)]
            public readonly partial record struct Account_Rev0([PrimaryKey] int Id, decimal Balance);
            """;

        var (asm, _) = GeneratorTestHost.CompileAndLoad(source);
        var opsType = asm.GetType("TestNs.Account_Rev0FrozenSchemaOps")!;

        Assert.That(opsType.GetMethod("SerializeVersionedMemoryPack", BindingFlags.Public | BindingFlags.Static), Is.Null);
        Assert.That(opsType.GetMethod("SerializeMessagePack", BindingFlags.Public | BindingFlags.Static), Is.Null);
    }
}
