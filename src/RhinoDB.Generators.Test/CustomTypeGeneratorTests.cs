using RhinoDB.SchemaContracts;

namespace RhinoDB.Generators.Test;

// CustomTypeGenerator (task #85): [CustomType] gives a standalone, non-[Table] record struct the same
// three-pipeline treatment a table row gets - a composable hand-rolled Raw writer/reader plus
// VersionedMemoryPack/MessagePack thin wrappers - and TableGenerator's own EmitWriteField/EmitReadField
// (task #86) route a row's [CustomType] field through that type's own Raw methods instead of MemoryPack's
// WriteValue<T>/ReadValue<T> generic dispatch.
public class CustomTypeGeneratorTests {
    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [CustomType]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct PlayerName(
            [property: MemoryPackOrder(0)] [property: Key(0)] string First,
            [property: MemoryPackOrder(1)] [property: Key(1)] string Last);

        [CustomType]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Address(
            [property: MemoryPackOrder(0)] [property: Key(0)] string City);

        [CustomType]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Contact(
            [property: MemoryPackOrder(0)] [property: Key(0)] string Email,
            [property: MemoryPackOrder(1)] [property: Key(1)] Address HomeAddress);

        [Table(TableKind.Instant, typeof(GameDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Player(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] PlayerName Name);
        """;

    [Test]
    public void StandaloneCustomType_SerializeRowThenDeserializeRow_RoundTrips() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var nameType = asm.GetType("TestNs.PlayerName")!;
        var name = Activator.CreateInstance(nameType, "Ada", "Lovelace")!;

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.PlayerNameCustomTypeOps", "SerializeRow", name)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.PlayerNameCustomTypeOps", "DeserializeRow", bytes);

        Assert.That(roundTripped, Is.EqualTo(name));
    }

    [Test]
    public void RowWithACustomTypeField_SerializeRowThenDeserializeRow_RoundTripsIncludingTheNestedType() {
        // This is the real proof of task #86's wiring: Player's OWN Raw serializer must call into
        // PlayerNameCustomTypeOps.WriteRaw/ReadRaw directly for the Name field, not MemoryPack's
        // WriteValue<T>/ReadValue<T> generic dispatch - if it fell through to WriteValue<T> here, this
        // would throw MemoryPackSerializationException, since GeneratorTestHost.CompileAndLoad only
        // runs TableGenerator/CustomTypeGenerator, not the real MemoryPack.Generator that WriteValue<T>
        // would need a formatter from.
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var playerType = asm.GetType("TestNs.Player")!;
        var nameType = asm.GetType("TestNs.PlayerName")!;
        var name = Activator.CreateInstance(nameType, "Grace", "Hopper")!;
        var player = Activator.CreateInstance(playerType, 1, name)!;

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbPlayerOps", "SerializeRow", player)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.GameDbPlayerOps", "DeserializeRow", bytes);

        Assert.That(roundTripped, Is.EqualTo(player));
    }

    [Test]
    public void NestedCustomTypeWithinACustomType_SerializeRowThenDeserializeRow_RoundTripsRecursively() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var contactType = asm.GetType("TestNs.Contact")!;
        var addressType = asm.GetType("TestNs.Address")!;
        var address = Activator.CreateInstance(addressType, "London")!;
        var contact = Activator.CreateInstance(contactType, "ada@example.com", address)!;

        var bytes = (byte[])GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.ContactCustomTypeOps", "SerializeRow", contact)!;
        var roundTripped = GeneratorTestHost.InvokePrivateStaticHelper(asm, "TestNs.ContactCustomTypeOps", "DeserializeRow", bytes);

        Assert.That(roundTripped, Is.EqualTo(contact));
    }

    [Test]
    public void CustomType_VersionedMemoryPackProtocol_OnlyThatWrapperPairIsGenerated() {
        // Proves the wrapper methods exist and are callable - full round-trip through the REAL
        // MemoryPack/MessagePack generators needs GeneratorTestHost.CompileAndLoadWithSerializationGenerators,
        // which hits the known upstream MemoryPack-vs-.NET-11-preview constraint issue (task #82) - this
        // just confirms CustomTypeGenerator emits the wrapper surface correctly.
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithClientProtocol(Source, ClientProtocolKind.VersionedMemoryPack);
        var opsType = asm.GetType("TestNs.PlayerNameCustomTypeOps")!;

        Assert.That(opsType.GetMethod("SerializeVersionedMemoryPack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), Is.Not.Null);
        Assert.That(opsType.GetMethod("DeserializeVersionedMemoryPack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), Is.Not.Null);
        Assert.That(opsType.GetMethod("SerializeMessagePack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), Is.Null);
    }

    [Test]
    public void CustomType_MessagePackProtocol_OnlyThatWrapperPairIsGenerated() {
        var (asm, _) = GeneratorTestHost.CompileAndLoadWithClientProtocol(Source, ClientProtocolKind.MessagePack);
        var opsType = asm.GetType("TestNs.PlayerNameCustomTypeOps")!;

        Assert.That(opsType.GetMethod("SerializeMessagePack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), Is.Not.Null);
        Assert.That(opsType.GetMethod("DeserializeMessagePack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), Is.Not.Null);
        Assert.That(opsType.GetMethod("SerializeVersionedMemoryPack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), Is.Null);
    }

    [Test]
    public void CustomType_RawProtocol_NeitherWrapperPairIsGenerated() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var opsType = asm.GetType("TestNs.PlayerNameCustomTypeOps")!;

        Assert.That(opsType.GetMethod("SerializeVersionedMemoryPack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), Is.Null);
        Assert.That(opsType.GetMethod("SerializeMessagePack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), Is.Null);
    }
}
