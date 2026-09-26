namespace RhinoDB.SchemaContracts.Test;

public class ClientProtocolParserTests {
    [TestCase("Raw", ClientProtocolKind.Raw)]
    [TestCase("VersionedMemoryPack", ClientProtocolKind.VersionedMemoryPack)]
    [TestCase("MessagePack", ClientProtocolKind.MessagePack)]
    public void Parse_ValidValue_ReturnsMatchingKind(string value, ClientProtocolKind expected) {
        Assert.That(ClientProtocolParser.Parse(value), Is.EqualTo(expected));
    }

    [Test]
    public void Parse_UnrecognizedValue_ThrowsNamingTheBadValueAndTheValidOnes() {
        var ex = Assert.Throws<GeneratorConfigException>(() => ClientProtocolParser.Parse("Bogus"));

        Assert.That(ex!.Message, Does.Contain("Bogus"));
        Assert.That(ex.Message, Does.Contain("Raw"));
        Assert.That(ex.Message, Does.Contain("VersionedMemoryPack"));
        Assert.That(ex.Message, Does.Contain("MessagePack"));
    }
}
