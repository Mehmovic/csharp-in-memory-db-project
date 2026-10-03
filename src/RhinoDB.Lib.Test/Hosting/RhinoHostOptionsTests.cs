using System.Globalization;

using RhinoDB.Core;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Hosting.Test;

public class RhinoHostOptionsTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-rhinohostoptions-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Test]
    public void FromConfig_WithOnlyColdPathSet_DefaultsToRunModeAndNoUpToLsn() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir }).Unwrap();

        Assert.That(result.ColdPath, Is.EqualTo(dir));
        Assert.That(result.Mode, Is.EqualTo(RhinoRunMode.Run));
        Assert.That(result.ReplayUpToLsn, Is.Null);
    }

    [Test]
    public void FromConfig_WithModeAndUpToLsn_ParsesBoth() {
        var result = RhinoHostOptions.FromConfig(
            new HostConfig { ColdPath = dir, Mode = "replay", ReplayUpToLsn = 42 }).Unwrap();

        Assert.That(result.Mode, Is.EqualTo(RhinoRunMode.Replay));
        Assert.That(result.ReplayUpToLsn, Is.EqualTo(42L));
    }

    [Test]
    public void FromConfig_WithAnUnknownMode_Fails() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, Mode = "bogus" });

        Assert.That(result.IsError(), Is.True);
    }

    [Test]
    public void FromConfig_WithBothWalKeepGenerationsAndWalPruneOlderThan_Fails() {
        var result = RhinoHostOptions.FromConfig(new HostConfig {
            ColdPath = dir, WalKeepGenerations = 3, WalPruneOlderThan = "2026-09-14T08:00:00Z",
        });

        Assert.That(result.IsError(), Is.True, "two retention policies at once is ambiguous.");
    }

    [Test]
    public void FromConfig_WithAnUnparseableWalPruneOlderThan_Fails() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, WalPruneOlderThan = "notatimestamp" });

        Assert.That(result.IsError(), Is.True);
    }

    // ---- ColdPath: absent means compute the default, never require it ----

    [Test]
    public void DefaultColdPath_IsUnderAppDataRhinoDbNamedAfterTheEntryAssembly() {
        var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RhinoDB");

        var defaultPath = RhinoHostOptions.DefaultColdPath();

        Assert.That(defaultPath, Does.StartWith(expectedRoot));
    }

    [Test]
    public void FromConfig_WithNoColdPathConfigured_UsesTheComputedDefault() {
        var result = RhinoHostOptions.FromConfig(new HostConfig());

        // Real I/O against the real computed default - accept whatever this machine resolves to
        // (CreateDirectory/write-probe must succeed under a normal developer/CI account), and clean
        // up afterward so running this test doesn't leave litter under the real AppData folder.
        try {
            Assert.That(result.IsOk(), Is.True);
            Assert.That(result.Unwrap().ColdPath, Is.EqualTo(RhinoHostOptions.DefaultColdPath()));
        } finally {
            try { Directory.Delete(RhinoHostOptions.DefaultColdPath(), recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ---- ColdPath accessibility: checked before running, fails loudly if unreachable ----

    [Test]
    public void EnsureColdPathAccessible_AWritableDirectory_Succeeds() {
        Assert.That(RhinoHostOptions.EnsureColdPathAccessible(dir).IsOk(), Is.True);
    }

    [Test]
    public void EnsureColdPathAccessible_CreatesTheDirectoryWhenMissing() {
        var missing = Path.Combine(dir, "not-created-yet");

        var result = RhinoHostOptions.EnsureColdPathAccessible(missing);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(Directory.Exists(missing), Is.True);
    }

    [Test]
    public void EnsureColdPathAccessible_APathUnderAnExistingFile_FailsWithAResultErrorNotAnUnhandledException() {
        // A file where a directory is expected - CreateDirectory throws IOException for this, not
        // UnauthorizedAccessException, so this also pins which exception types are actually caught.
        var blockingFile = Path.Combine(dir, "blocking-file");
        File.WriteAllText(blockingFile, "");
        var unreachable = Path.Combine(blockingFile, "cold");

        var result = RhinoHostOptions.EnsureColdPathAccessible(unreachable);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
    }

    // ---- http-port / http-enabled ----

    [Test]
    public void FromConfig_InRunMode_DefaultsHttpEnabledToTrueAndPortTo7777() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir }).Unwrap();

        Assert.That(result.HttpEnabled, Is.True, "running the database starts the server by default.");
        Assert.That(result.HttpPort, Is.EqualTo(7777));
    }

    [Test]
    public void FromConfig_InReplayMode_DefaultsHttpEnabledToFalse() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, Mode = "replay" }).Unwrap();

        Assert.That(result.HttpEnabled, Is.False, "maintenance/tooling invocations are not \"running the database\".");
    }

    [Test]
    public void FromConfig_InMigrateMode_DefaultsHttpEnabledToFalse() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, Mode = "migrate" }).Unwrap();

        Assert.That(result.HttpEnabled, Is.False);
    }

    [Test]
    public void FromConfig_InWalPruneMode_DefaultsHttpEnabledToFalse() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, Mode = "wal-prune" }).Unwrap();

        Assert.That(result.HttpEnabled, Is.False);
    }

    [Test]
    public void FromConfig_InWalMigrateMode_DefaultsHttpEnabledToFalse() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, Mode = "wal-migrate" }).Unwrap();

        Assert.That(result.HttpEnabled, Is.False);
    }

    [Test]
    public void FromConfig_WithHttpEnabledExplicitlyTrue_OverridesAModeThatWouldOtherwiseDefaultToFalse() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, Mode = "replay", HttpEnabled = true }).Unwrap();

        Assert.That(result.HttpEnabled, Is.True, "an explicit override must win over the mode-based default.");
    }

    [Test]
    public void FromConfig_WithHttpEnabledExplicitlyFalse_OverridesRunModesDefaultOfTrue() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, HttpEnabled = false }).Unwrap();

        Assert.That(result.HttpEnabled, Is.False);
    }

    [Test]
    public void FromConfig_WithAnExplicitHttpPort_Parses() {
        var result = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, HttpPort = 9000 }).Unwrap();

        Assert.That(result.HttpPort, Is.EqualTo(9000));
    }

    // ---- Load: reads the real rdbsettings.json file ----

    [Test]
    public void Load_ReadsTheHostSectionFromARealFile() {
        RhinoHostConfigTestHelper.WriteConfig(dir, new HostConfig { ColdPath = dir, HttpPort = 9001 });

        var result = RhinoHostOptions.Load(dir).Unwrap();

        Assert.That(result.ColdPath, Is.EqualTo(dir));
        Assert.That(result.HttpPort, Is.EqualTo(9001));
    }

    [Test]
    public void Load_WithNoFilePresent_WritesADefaultAndStillResolves() {
        // GeneratorConfigLoader.LoadFull already auto-creates a default file when one is missing -
        // RhinoHostOptions.Load must still resolve cleanly against that default (no ColdPath
        // configured -> the computed default), not fail just because nothing was ever written.
        try {
            var result = RhinoHostOptions.Load(dir);

            Assert.That(result.IsOk(), Is.True);
            Assert.That(File.Exists(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName)), Is.True);
        } finally {
            try { Directory.Delete(RhinoHostOptions.DefaultColdPath(), recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ---- ResolveUtcTicks: unrelated to the Host section, still used directly by RhinoDB.Tools.Wal ----

    [Test]
    public void ResolveUtcTicks_AnUnqualifiedTimestamp_IsReadAsLocalTimeNotAsUtc() {
        // The bug this pins: treating a bare "08:00" as UTC shifts a UTC+02:00 operator's intent
        // by two hours, and a prune cutoff two hours wrong deletes the wrong segments.
        var wallClock = new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Unspecified);
        var expected = new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock)).UtcTicks;
        var text = wallClock.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var resolved = RhinoHostOptions.ResolveUtcTicks(text).Unwrap();

        Assert.That(resolved, Is.EqualTo(expected));
    }

    [Test]
    public void ResolveUtcTicks_AZuluSuffix_IsTakenAsUtcVerbatim() {
        var text = "2026-09-14T08:00:00Z";

        var resolved = RhinoHostOptions.ResolveUtcTicks(text).Unwrap();

        Assert.That(resolved, Is.EqualTo(new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero).UtcTicks));
    }

    [Test]
    public void ResolveUtcTicks_AnExplicitOffset_IsHonouredAndConvertedToUtc() {
        // +02:00 at 08:00 wall clock is 06:00 UTC - and must NOT be reinterpreted as local.
        var resolved = RhinoHostOptions.ResolveUtcTicks("2026-09-14T08:00:00+02:00").Unwrap();

        Assert.That(resolved, Is.EqualTo(new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.Zero).UtcTicks));
    }

    [Test]
    public void ResolveUtcTicks_AnExplicitOffsetWinsEvenWhenItDisagreesWithTheLocalZone() {
        // Deterministic regardless of where the test runs: -05:00 is never this machine's offset
        // in a way that could accidentally produce the same answer, and the point is the offset
        // in the text is authoritative.
        var resolved = RhinoHostOptions.ResolveUtcTicks("2026-09-14T08:00:00-05:00").Unwrap();

        Assert.That(resolved, Is.EqualTo(new DateTimeOffset(2026, 9, 14, 13, 0, 0, TimeSpan.Zero).UtcTicks));
    }

    [Test]
    public void ResolveUtcTicks_AnUnparseableValue_FailsLoudlyWithTheFlagNameInTheMessage() {
        var result = RhinoHostOptions.ResolveUtcTicks("last tuesday-ish", "--game.wal-prune-older-than");

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.SystemFailure));
        var thrown = Assert.Throws<ArgumentException>(result.ThrowIfError);
        Assert.That(thrown!.Message, Does.Contain("--game.wal-prune-older-than"),
            "the operator has to be able to tell WHICH flag was wrong, and which forms are accepted.");
    }

    [Test]
    public void ResolveUtcTicks_AMissingValue_FailsRatherThanDefaultingToSomething() {
        Assert.That(RhinoHostOptions.ResolveUtcTicks(null).IsError(), Is.True);
        Assert.That(RhinoHostOptions.ResolveUtcTicks("   ").IsError(), Is.True);
    }

    [Test]
    public void FromConfig_PruneOlderThanWithoutAnOffset_StoresTheLocallyResolvedUtcTicks() {
        var wallClock = new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Unspecified);
        var expected = new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock)).UtcTicks;
        var text = wallClock.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var parsed = RhinoHostOptions.FromConfig(new HostConfig { ColdPath = dir, WalPruneOlderThan = text }).Unwrap();

        Assert.That(parsed.WalPruneOlderThanUtcTicks, Is.EqualTo(expected));
    }
}
