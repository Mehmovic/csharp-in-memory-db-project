using System.Text.Json;

namespace RhinoDB.SchemaContracts.Test;

// rdbsettings.json is the one place every option lives - the host reads it at startup, the generators and analyzers
// read the same file at compile time (RhinoSettingsSnapshot).
public class RhinoSettingsTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Test]
    public void TheDefaultFile_WritesEverySectionAndOption_SoDevelopersSeeWhatCanBeSet() {
        GeneratorConfigLoader.LoadFull(dir);

        using var written = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName)));
        var root = written.RootElement;

        Assert.Multiple(() => {
            Assert.That(root.GetProperty("Generator").GetProperty("ClientProtocol").GetString(), Is.EqualTo("Raw"));
            Assert.That(root.GetProperty("Network").GetProperty("Transport").GetString(), Is.EqualTo("WebSocket"));
            Assert.That(root.GetProperty("Durability").GetProperty("WalFlushThresholdBytes").GetInt64(), Is.EqualTo(4 * 1024 * 1024));
            Assert.That(root.GetProperty("Durability").GetProperty("WalFlushInterval").GetString(), Is.EqualTo("00:00:00.100"));
            Assert.That(root.GetProperty("Durability").GetProperty("EvictionBatchThresholdBytes").GetInt64(), Is.EqualTo(4 * 1024 * 1024));
            Assert.That(root.GetProperty("Host").GetProperty("UnrecoverableError").GetProperty("ShutdownBudget").GetString(), Is.EqualTo("00:00:05"));
            Assert.That(root.GetProperty("Host").GetProperty("UnrecoverableError").GetProperty("ExitWatchdog").GetString(), Is.EqualTo("00:00:07"));
            Assert.That(root.GetProperty("Server").GetProperty("ArchiveRetention").GetProperty("Enabled").GetBoolean(), Is.False);
            Assert.That(root.GetProperty("Prefs").GetProperty("CacheDuration").GetString(), Is.EqualTo("00:30:00"));
        });
    }

    [Test]
    public void TheDefaultFile_HasNoComputedProperties() {
        GeneratorConfigLoader.LoadFull(dir);

        var text = File.ReadAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName));

        Assert.That(text, Does.Not.Contain("PackedVersion").And.Not.Contain("Resolved"), "derived values are not settings - writing them invites edits that do nothing.");
    }

    [Test]
    public void TheDefaultFile_RoundTripsToTheDefaults() {
        GeneratorConfigLoader.LoadFull(dir);

        var reread = GeneratorConfigLoader.LoadFull(dir);

        Assert.That(reread.Network.Transport, Is.EqualTo("WebSocket"));
        Assert.That(reread.Durability.ResolvedWalFlushInterval, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
        Assert.That(reread.Host.UnrecoverableError.ResolvedShutdownBudget, Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    // ---- the compile-time snapshot ----

    [TestCase(null)]
    [TestCase("")]
    public void Snapshot_ForAMissingOrEmptyFile_IsTheDefaults(string? json) {
        Assert.That(RhinoSettingsSnapshot.Read(json), Is.EqualTo(RhinoSettingsSnapshot.Default));
    }

    [Test]
    public void Snapshot_ReadsTheClientProtocolAndTheTransport() {
        var snapshot = RhinoSettingsSnapshot.Read("""{ "Generator": { "ClientProtocol": "MessagePack" }, "Network": { "Transport": "WebSocket" } }""");

        Assert.That(snapshot.ClientProtocol, Is.EqualTo(ClientProtocolKind.MessagePack));
        Assert.That(snapshot.Transport, Is.EqualTo(NetworkTransportKind.WebSocket));
        Assert.That(snapshot.ParseError, Is.Null);
    }

    [TestCase("{ not json")]
    [TestCase("""{ "Network": { "Transport": "CarrierPigeon" } }""")]
    [TestCase("""{ "Generator": { "ClientProtocol": "Bogus" } }""")]
    public void Snapshot_ForABadFile_FallsBackToDefaults_AndCarriesTheError(string json) {
        var snapshot = RhinoSettingsSnapshot.Read(json);

        Assert.That(snapshot.ClientProtocol, Is.EqualTo(ClientProtocolKind.Raw));
        Assert.That(snapshot.Transport, Is.EqualTo(NetworkTransportKind.WebSocket));
        Assert.That(snapshot.ParseError, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Snapshot_IsValueEqual_SoIncrementalGeneratorsCacheOnIt() {
        const string json = """{ "Generator": { "ClientProtocol": "VersionedMemoryPack" } }""";

        Assert.That(RhinoSettingsSnapshot.Read(json), Is.EqualTo(RhinoSettingsSnapshot.Read(json)));
        Assert.That(RhinoSettingsSnapshot.Read(json).GetHashCode(), Is.EqualTo(RhinoSettingsSnapshot.Read(json).GetHashCode()));
    }

    [Test]
    public void NetworkTransportParser_RejectsAnUnknownTransport_NamingTheValidOne() {
        var ex = Assert.Throws<GeneratorConfigException>(() => NetworkTransportParser.Parse("Udp"));

        Assert.That(ex!.Message, Does.Contain("Udp").And.Contain("WebSocket"));
    }

    [TestCase("nonsense")]
    [TestCase("00:00:00")]
    [TestCase("-00:00:01")]
    public void ConfigDuration_RejectsInvalidOrNonPositiveDurations_NamingTheSetting(string value) {
        var ex = Assert.Throws<GeneratorConfigException>(() => ConfigDuration.ParseRequired(value, "Durability.WalFlushInterval"));

        Assert.That(ex!.Message, Does.Contain("Durability.WalFlushInterval"));
    }
}
