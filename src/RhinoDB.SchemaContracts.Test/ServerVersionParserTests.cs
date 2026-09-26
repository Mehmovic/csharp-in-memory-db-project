namespace RhinoDB.SchemaContracts.Test;

public class ServerVersionParserTests {
    [Test]
    public void Parse_ValidVersionString_PacksMajorMinorPatchInOrder() {
        var packed = ServerVersionParser.Parse("1.2.300");

        Assert.That(packed, Is.EqualTo(((uint)1 << 24) | ((uint)2 << 16) | 300));
    }

    [Test]
    public void Parse_ZeroZeroZero_PacksToZero() {
        Assert.That(ServerVersionParser.Parse("0.0.0"), Is.EqualTo(0u));
    }

    [Test]
    public void Parse_HigherMajor_AlwaysPacksLarger() {
        Assert.That(ServerVersionParser.Parse("2.0.0"), Is.GreaterThan(ServerVersionParser.Parse("1.255.65535")));
    }

    [TestCase("1.2")]
    [TestCase("1.2.3.4")]
    [TestCase("")]
    public void Parse_WrongSegmentCount_ThrowsGeneratorConfigException(string version) {
        Assert.Throws<GeneratorConfigException>(() => ServerVersionParser.Parse(version));
    }

    [TestCase("256.0.0")]
    [TestCase("0.256.0")]
    [TestCase("0.0.65536")]
    [TestCase("a.b.c")]
    [TestCase("-1.0.0")]
    public void Parse_OutOfRangeOrNonNumericSegment_ThrowsGeneratorConfigException(string version) {
        Assert.Throws<GeneratorConfigException>(() => ServerVersionParser.Parse(version));
    }
}
