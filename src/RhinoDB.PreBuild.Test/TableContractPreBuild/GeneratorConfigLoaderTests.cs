using RhinoDB.PreBuild;

namespace RhinoDB.PreBuild.Test;

public class GeneratorConfigLoaderTests {
    string tempDir = "";

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
        var config = GeneratorConfigLoader.Parse("""{"SourceParentDirectory": "MySource", "TargetParentDirectory": "MyOutput"}""");

        Assert.That(config.SourceParentDirectory, Is.EqualTo("MySource"));
        Assert.That(config.TargetParentDirectory, Is.EqualTo("MyOutput"));
    }

    [Test]
    public void Parse_PartialJson_MissingFieldFallsBackToItsDefault() {
        var config = GeneratorConfigLoader.Parse("""{"TargetParentDirectory": "MyOutput"}""");

        Assert.That(config.SourceParentDirectory, Is.EqualTo("RhinoContracts"));
        Assert.That(config.TargetParentDirectory, Is.EqualTo("MyOutput"));
    }

    [Test]
    public void Parse_CaseInsensitivePropertyNames_StillMatches() {
        var config = GeneratorConfigLoader.Parse("""{"sourceparentdirectory": "lower"}""");

        Assert.That(config.SourceParentDirectory, Is.EqualTo("lower"));
    }

    [Test]
    public void Parse_MalformedJson_ThrowsGeneratorConfigException() {
        Assert.Throws<GeneratorConfigException>(() => GeneratorConfigLoader.Parse("{ not valid json"));
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
        File.WriteAllText(configPath, """{"SourceParentDirectory": "Custom", "TargetParentDirectory": "Generated"}""");

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
}
