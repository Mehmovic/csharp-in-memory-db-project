namespace RhinoDB.SchemaContracts.Test;

// config.json is one unified file, sections keyed by concern ("Generator", "Server", ...) rather than one
// file per concern - GeneratorConfigLoader.Load/Parse stay shaped exactly like before (return just
// GeneratorConfig) for every existing caller; LoadFull/ParseFull expose the whole RhinoDbConfig for callers
// that need other sections too.
public class GeneratorConfigLoaderTests {
    private string tempDir = "";

    [SetUp]
    public void SetUp() {
        tempDir = Path.Combine(Path.GetTempPath(), "rhinodb-generatorconfig-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    [Test]
    public void Parse_ValidJsonWithBothFields_ReturnsThem() {
        var config = GeneratorConfigLoader.Parse("""{"Generator": {"SourceParentDirectory": "MySource", "TargetParentDirectory": "MyOutput"}}""");

        Assert.That(config.SourceParentDirectory, Is.EqualTo("MySource"));
        Assert.That(config.TargetParentDirectory, Is.EqualTo("MyOutput"));
    }

    [Test]
    public void Parse_PartialJson_MissingFieldFallsBackToItsDefault() {
        var config = GeneratorConfigLoader.Parse("""{"Generator": {"TargetParentDirectory": "MyOutput"}}""");

        Assert.That(config.SourceParentDirectory, Is.EqualTo("RhinoContracts"));
        Assert.That(config.TargetParentDirectory, Is.EqualTo("MyOutput"));
    }

    [Test]
    public void Parse_NoGeneratorSectionAtAll_FallsBackToAllDefaults() {
        var config = GeneratorConfigLoader.Parse("{}");

        Assert.That(config.SourceParentDirectory, Is.EqualTo("RhinoContracts"));
        Assert.That(config.TargetParentDirectory, Is.EqualTo("RhinoDB"));
    }

    [Test]
    public void Parse_CaseInsensitivePropertyNames_StillMatches() {
        var config = GeneratorConfigLoader.Parse("""{"generator": {"sourceparentdirectory": "lower"}}""");

        Assert.That(config.SourceParentDirectory, Is.EqualTo("lower"));
    }

    [Test]
    public void Parse_MalformedJson_ThrowsGeneratorConfigException() {
        Assert.Throws<GeneratorConfigException>(() => GeneratorConfigLoader.Parse("{ not valid json"));
    }

    [Test]
    public void Parse_NoClientProtocolField_DefaultsToRaw() {
        var config = GeneratorConfigLoader.Parse("""{"Generator": {"TargetParentDirectory": "MyOutput"}}""");

        Assert.That(config.ClientProtocol, Is.EqualTo("Raw"));
    }

    [Test]
    public void Parse_ExplicitClientProtocol_RoundTrips() {
        var config = GeneratorConfigLoader.Parse("""{"Generator": {"ClientProtocol": "MessagePack"}}""");

        Assert.That(config.ClientProtocol, Is.EqualTo("MessagePack"));
    }

    [Test]
    public void ParseFull_ServerSectionAlongsideGenerator_BothParseIndependently() {
        var full = GeneratorConfigLoader.ParseFull("""
            {"Generator": {"ClientProtocol": "MessagePack"}, "Server": {"Version": "2.3.1"}}
            """);

        Assert.That(full.Generator.ClientProtocol, Is.EqualTo("MessagePack"));
        Assert.That(full.Server.Version, Is.EqualTo("2.3.1"));
        Assert.That(full.Server.PackedVersion, Is.EqualTo(ServerVersionParser.Parse("2.3.1")));
    }

    [Test]
    public void Load_NoConfigFilePresent_ReturnsDefaultsAndWritesTheFileToDisk() {
        Assert.That(File.Exists(Path.Combine(tempDir, GeneratorConfigLoader.ConfigFileName)), Is.False);

        var config = GeneratorConfigLoader.Load(tempDir);

        Assert.That(config.SourceParentDirectory, Is.EqualTo("RhinoContracts"));
        Assert.That(config.TargetParentDirectory, Is.EqualTo("RhinoDB"));

        var writtenPath = Path.Combine(tempDir, GeneratorConfigLoader.ConfigFileName);
        Assert.That(File.Exists(writtenPath), Is.True, "Load() should scaffold a default config file when none exists.");

        var reparsed = GeneratorConfigLoader.Parse(File.ReadAllText(writtenPath));
        Assert.That(reparsed.SourceParentDirectory, Is.EqualTo("RhinoContracts"));
        Assert.That(reparsed.TargetParentDirectory, Is.EqualTo("RhinoDB"));
    }

    [Test]
    public void Load_ExistingConfigFilePresent_ReadsItAndDoesNotOverwriteIt() {
        var configPath = Path.Combine(tempDir, GeneratorConfigLoader.ConfigFileName);
        File.WriteAllText(configPath, """{"Generator": {"SourceParentDirectory": "Custom", "TargetParentDirectory": "Generated"}}""");

        var config = GeneratorConfigLoader.Load(tempDir);

        Assert.That(config.SourceParentDirectory, Is.EqualTo("Custom"));
        Assert.That(config.TargetParentDirectory, Is.EqualTo("Generated"));
        Assert.That(File.ReadAllText(configPath), Does.Contain("Custom"), "An existing config file must not be overwritten.");
    }

    [Test]
    public void Load_SecondCallAfterScaffolding_ReadsBackTheScaffoldedDefaults() {
        GeneratorConfigLoader.Load(tempDir);
        var second = GeneratorConfigLoader.Load(tempDir);

        Assert.That(second.SourceParentDirectory, Is.EqualTo("RhinoContracts"));
        Assert.That(second.TargetParentDirectory, Is.EqualTo("RhinoDB"));
    }

    [Test]
    public void LoadFull_NoConfigFilePresent_ScaffoldsBothGeneratorAndServerSections() {
        var full = GeneratorConfigLoader.LoadFull(tempDir);

        Assert.That(full.Generator, Is.Not.Null);
        Assert.That(full.Server, Is.Not.Null);

        var writtenPath = Path.Combine(tempDir, GeneratorConfigLoader.ConfigFileName);
        var scaffolded = File.ReadAllText(writtenPath);
        Assert.That(scaffolded, Does.Contain("Generator"));
        Assert.That(scaffolded, Does.Contain("Server"));
    }
}
