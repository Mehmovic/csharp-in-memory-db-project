using RhinoDB.Core;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Tables;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Durability.Test;

// The online archive collector. Two things are being pinned here:
//
//   1. The POLICY arithmetic - "keep a month" becomes an absolute UTC cutoff, and the two knobs
//      (how often, how much survives) are validated rather than silently defaulted.
//   2. That a collection pass really removes whole old segments and really leaves newer ones.
//
// The collector schedules itself as an ordinary Run, which is what serializes it against a
// checkpoint's archive write - see ArchiveRetentionCollector's comment for why that matters.
public class ArchiveRetentionTests {
    static private readonly long January = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    static private readonly long March = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    // ---- policy: the two knobs ----

    [Test]
    public void FromConfig_EnabledWithBothKnobs_YieldsThatPolicy() {
        var config = new ArchiveRetentionConfig { Enabled = true, Interval = "04:00:00", KeepFor = "30.00:00:00" };

        var policy = ArchiveRetentionPolicy.FromConfig(config);

        Assert.That(policy, Is.Not.Null);
        Assert.That(policy!.Interval, Is.EqualTo(TimeSpan.FromHours(4)), "Interval is HOW OFTEN.");
        Assert.That(policy.KeepFor, Is.EqualTo(TimeSpan.FromDays(30)), "KeepFor is HOW MUCH SURVIVES.");
    }

    [Test]
    public void FromConfig_AbsentOrDisabled_MeansNeverCollect() {
        Assert.That(ArchiveRetentionPolicy.FromConfig(null), Is.Null);
        Assert.That(ArchiveRetentionPolicy.FromConfig(new ArchiveRetentionConfig { Interval = "04:00:00", KeepFor = "30.00:00:00" }), Is.Null);
        // (enabled-but-incomplete is an error, not a silent no-op - see the test below)
    }

    [Test]
    public void FromConfig_EnabledButMissingAKnob_FailsLoudly() {
        var missingKeepFor = new ArchiveRetentionConfig { Enabled = true, Interval = "04:00:00" };
        var missingInterval = new ArchiveRetentionConfig { Enabled = true, KeepFor = "30.00:00:00" };

        Assert.Throws<GeneratorConfigException>(() => ArchiveRetentionPolicy.FromConfig(missingKeepFor));
        Assert.Throws<GeneratorConfigException>(() => ArchiveRetentionPolicy.FromConfig(missingInterval));
    }

    [Test]
    public void FromConfig_ZeroOrNegativeKnobs_FailLoudly() {
        Assert.Throws<GeneratorConfigException>(() =>
            ArchiveRetentionPolicy.FromConfig(new ArchiveRetentionConfig { Enabled = true, Interval = "00:00:00", KeepFor = "30.00:00:00" }));
        Assert.Throws<GeneratorConfigException>(() =>
            ArchiveRetentionPolicy.FromConfig(new ArchiveRetentionConfig { Enabled = true, Interval = "04:00:00", KeepFor = "-1.00:00:00" }));
    }

    [Test]
    public void FromConfig_AMalformedDuration_FailsWithAUsefulMessage() {
        var config = new ArchiveRetentionConfig { Enabled = true, Interval = "4h", KeepFor = "30.00:00:00" };

        var ex = Assert.Throws<GeneratorConfigException>(() => ArchiveRetentionPolicy.FromConfig(config));

        Assert.That(ex!.Message, Does.Contain("4h"));
        Assert.That(ex.Message, Does.Contain("Server.ArchiveRetention.Interval"));
    }

    [Test]
    public void CutoffUtcTicks_IsNowMinusKeepFor() {
        var policy = new ArchiveRetentionPolicy(TimeSpan.FromHours(4), TimeSpan.FromDays(30));
        var now = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

        var cutoff = policy.CutoffUtcTicks(now);

        Assert.That(cutoff, Is.EqualTo(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero).UtcTicks));
    }

    // ---- a real collection pass ----

    private string CreateStore() {
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static private DecodedWalEntry StampedEntry(long lsn, long utcTicks) =>
        new(lsn, WalEntryKind.Operation, [new WalChange(1, ChangeKind.Insert, BitConverter.GetBytes(lsn), [9])], utcTicks);

    private void WriteSegments(string coldPath, params (long ticks, uint generation)[] segments) {
        var archiveDir = Path.Combine(coldPath, WalArchive.ArchiveDirectoryName);
        foreach (var segment in segments)
            Assert.That(WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [StampedEntry(1, segment.ticks)], segment.generation), Is.Null);
    }

    private string[] SegmentFiles(string coldPath) {
        var archiveDir = Path.Combine(coldPath, WalArchive.ArchiveDirectoryName);
        return Directory.Exists(archiveDir) ? Directory.GetFiles(archiveDir, "*.wal") : [];
    }

    [Test]
    public async Task ACollectionPass_RemovesOnlySegmentsOlderThanTheWindow() {
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            WriteSegments(coldPath, (January, 1), (March, 2));
            var keepFor = TimeSpan.FromDays(1);
            var now = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
            var cutoff = new ArchiveRetentionPolicy(TimeSpan.FromHours(4), keepFor).CutoffUtcTicks(now);

            var deleted = cold.PruneArchiveOlderThan(cutoff);

            Assert.That(deleted.Unwrap(), Is.EqualTo(1));
            Assert.That(SegmentFiles(coldPath), Has.Length.EqualTo(1));
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    [Test]
    public void ACollectionPass_AdvancesTheFloorAndLeavesHistoryReadable() {
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            WriteSegments(coldPath, (January, 4), (March, 5));
            var now = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
            var cutoff = new ArchiveRetentionPolicy(TimeSpan.FromHours(4), TimeSpan.FromDays(1)).CutoffUtcTicks(now);

            cold.PruneArchiveOlderThan(cutoff);

            Assert.That(cold.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(5));
            var history = WalArchive.ReadHistory(coldPath, [], 0).Unwrap();
            Assert.That(history, Has.Count.EqualTo(1), "the surviving segment must still replay cleanly");
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    [Test]
    public void ACollectionPass_KeepsASegmentHoldingUnstampedEntries() {
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            var archiveDir = Path.Combine(coldPath, WalArchive.ArchiveDirectoryName);
            // A hand-built entry has no stamp, so its age cannot be proven - never deleted on a guess.
            WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [new DecodedWalEntry(1, WalEntryKind.Operation, [new WalChange(1, ChangeKind.Insert, [1], [9])])], 1);
            var farFuture = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var deleted = cold.PruneArchiveOlderThan(new ArchiveRetentionPolicy(TimeSpan.FromHours(4), TimeSpan.FromDays(1)).CutoffUtcTicks(farFuture));

            Assert.That(deleted.Unwrap(), Is.EqualTo(0));
            Assert.That(SegmentFiles(coldPath), Has.Length.EqualTo(1));
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    [Test]
    public void ACollectionPass_LeavesTheLiveWalUntouched() {
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            WriteSegments(coldPath, (January, 1));

            // The WAL is held open by the ColdStore with FileShare.None, so the only honest
            // assertion is that the collector did not shorten or remove it.
            var walPath = Path.Combine(coldPath, "wal.dat");
            Assert.That(File.Exists(walPath), Is.True, "ColdStore.Open creates the live WAL.");
            var lengthBefore = new FileInfo(walPath).Length;

            cold.PruneArchiveOlderThan(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks);

            Assert.That(File.Exists(walPath), Is.True, "the collector must NEVER touch the live WAL.");
            Assert.That(new FileInfo(walPath).Length, Is.EqualTo(lengthBefore));
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    // ---- Enabled: false must not construct the collector, and therefore not the Timer ----

    [Test]
    public void FromConfig_AConfigJsonSayingEnabledFalse_YieldsNoPolicy() {
        // The exact shape an application would load and hand to FromConfig.
        var json = """{ "Server": { "Version": "1.0.0", "ArchiveRetention": { "Enabled": false, "Interval": "04:00:00", "KeepFor": "30.00:00:00" } } }""";

        var config = GeneratorConfigLoader.ParseFull(json);
        var policy = ArchiveRetentionPolicy.FromConfig(config.Server.ArchiveRetention);

        Assert.That(policy, Is.Null,
            "Enabled: false means no policy - which is what stops the collector from ever being built.");
    }

    [Test]
    public void FromConfig_EnabledFalseWithAGarbageDuration_StillReturnsNullWithoutThrowing() {
        // A disabled section is not validated: the durations are irrelevant, so a stale broken
        // value cannot stop a server from starting.
        var json = """{ "Server": { "ArchiveRetention": { "Enabled": false, "Interval": "nonsense", "KeepFor": "also-nonsense" } } }""";

        var config = GeneratorConfigLoader.ParseFull(json);
        var policy = ArchiveRetentionPolicy.FromConfig(config.Server.ArchiveRetention);

        Assert.That(policy, Is.Null);
    }

    [Test]
    public void FromConfig_EnabledTrueIsTheOnlyWayToGetAPolicy() {
        var off = ArchiveRetentionPolicy.FromConfig(new ArchiveRetentionConfig { Enabled = false, Interval = "01:00:00", KeepFor = "01.00:00:00" });
        var missingSection = ArchiveRetentionPolicy.FromConfig(null);
        var defaultCtor = ArchiveRetentionPolicy.FromConfig(new ArchiveRetentionConfig { Interval = "01:00:00", KeepFor = "01.00:00:00" });

        Assert.Multiple(() => {
            Assert.That(off, Is.Null);
            Assert.That(missingSection, Is.Null);
            Assert.That(defaultCtor, Is.Null, "Enabled defaults to false, so a section without it is off.");
        });
    }

    [Test]
    public async Task ADisabledPolicy_StartsNoCollectorAndNeverFiresARun() {
        // The strongest available statement: with a 40ms interval an ENABLED collector fires
        // repeatedly, and a disabled one never fires at all - which is only possible if no Timer
        // was ever scheduled.
        var dir = Path.Combine(Path.GetTempPath(), "rhinodb-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var off = ArchiveRetentionPolicy.FromConfig(new ArchiveRetentionConfig { Enabled = false, Interval = "00:00:00.040", KeepFor = "00:00:00.010" });
            var on = new ArchiveRetentionPolicy(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(10));

            var builder = RhinoHostBuilder.Create([$"--game.cold-path={dir}"]);
            var runs = 0;
            builder.OnRetentionRun += (_, _) => Interlocked.Increment(ref runs);
            builder.AddDatabase<NoRetentionDb, DefaultTransaction>("game", options => {
                options.CreateDb = cold => new NoRetentionDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
                options.ArchiveRetention = off;      // the disabled policy, from real config semantics
            });

            using var host = (await builder.BuildAsync()).Unwrap();
            host.GetDatabase<NoRetentionDb>("game").Cold!.Dispose();

            Assert.That(host.ArchiveCollectorCount, Is.EqualTo(0), "a disabled policy builds no collector.");
            await Task.Delay(300);
            Assert.That(Volatile.Read(ref runs), Is.EqualTo(0),
                "A 40ms interval over 300ms would have fired ~7 times had any Timer existed. Zero runs " +
                "means no Timer was ever scheduled - not a timer that exists and does nothing.");

            // Control: the same wiring with retention ON does fire, so the assertion above is
            // about the disabled policy and not about the harness never firing.
            var controlRuns = 0;
            var controlBuilder = RhinoHostBuilder.Create([$"--game.cold-path={dir}"]);
            controlBuilder.OnRetentionRun += (_, _) => Interlocked.Increment(ref controlRuns);
            controlBuilder.AddDatabase<NoRetentionDb, DefaultTransaction>("game", options => {
                options.CreateDb = cold => new NoRetentionDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
                options.ArchiveRetention = on;
            });

            using var controlHost = (await controlBuilder.BuildAsync()).Unwrap();
            controlHost.GetDatabase<NoRetentionDb>("game").Cold!.Dispose();
            await Task.Delay(300);

            Assert.That(Volatile.Read(ref controlRuns), Is.GreaterThan(0),
                "sanity: the same wiring WITH retention fires, so a zero above means disabled, not broken.");
            controlHost.Dispose();
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class NoRetentionDb(RhinoDB.Lib.Cold.ColdStore cold) : RhinoDB.Lib.Execution.DbContext(cold);
}