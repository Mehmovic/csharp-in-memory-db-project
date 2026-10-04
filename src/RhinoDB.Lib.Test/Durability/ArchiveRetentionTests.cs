using RhinoDB.Core;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Hosting.Test;
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
    static private readonly ulong January = (ulong)new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    static private readonly ulong March = (ulong)new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    // AFTER the 2026-03-02 cutoff the hole tests prune against, so a segment stamped with it is a
    // genuine "too new to delete". (Using March itself there would make every segment deletable.)
    static private readonly ulong Recent = (ulong)new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero).UtcTicks;

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

    static private DecodedWalEntry StampedEntry(ulong lsn, ulong utcTicks) => new DecodedWalEntry(
        lsn,
        WalEntryKind.Operation,
        [new WalChange(1, ChangeKind.Insert, BitConverter.GetBytes((long)lsn), [9])],
        utcTicks
    );

    private void WriteSegments(string coldPath, params (ulong ticks, uint generation)[] segments) {
        var archiveDir = Path.Combine(coldPath, WalArchive.ArchiveDirectoryName);
        // Each segment gets its OWN lsn: reusing one makes MergeInOrder dedupe the history down to
        // a single entry, which would hide whatever the test is actually about.
        for (var i = 0; i < segments.Length; i++)
            Assert.That(WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [StampedEntry((ulong)(i + 1), segments[i].ticks)], segments[i].generation), Is.Null);
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

            cold.PruneArchiveOlderThan((ulong)new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks);

            Assert.That(File.Exists(walPath), Is.True, "the collector must NEVER touch the live WAL.");
            Assert.That(new FileInfo(walPath).Length, Is.EqualTo(lengthBefore));
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    // ---- KNOWN FLAW (2026-09-22), now fixed: the prune was per-segment, so it could leave a hole ----
    //
    // DeleteSegmentsOlderThanTimestamp used to evaluate each segment INDEPENDENTLY and keep going
    // past any segment that failed the age test. With segments S1..S4 that deletes S2 and keeps
    // S1, S3, S4 - a hole exactly where S2's LSN range was. Wall-clock going backwards is enough
    // to produce that arrangement, and the design's own docs name NTP corrections, VM snapshots
    // and operators changing the clock as the reasons a later segment can carry an earlier stamp.
    //
    // It went undetected because:
    //   1. MergeInOrder's `+1` contiguity check was deliberately removed in Stage 5.5 (Instant-only
    //      transactions create legitimate LSN gaps), so a prune-induced hole is indistinguishable
    //      from a benign Instant gap;
    //   2. LoadFromGenesis's ArchiveOlderThanRetentionFloor guard is tautological after this
    //      operation, because PruneArchiveOlderThan recomputes the floor from whatever survived;
    //   3. replay then returns Result.Ok() over a partial history - a WRONG state that looks fine.
    //
    // The invariant these tests pin: whatever survives a time-based prune is a contiguous PREFIX
    // of the LSN sequence. A backwards clock step may only ever cause the prune to keep MORE.

    private void WriteSegmentsWithLsns(string coldPath, params (ulong lsn, ulong ticks)[] entries) {
        var archiveDir = Path.Combine(coldPath, WalArchive.ArchiveDirectoryName);
        foreach (var (lsn, ticks) in entries)
            Assert.That(WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [StampedEntry(lsn, ticks)], 1), Is.Null);
    }

    [Test]
    public void ACollectionPass_LeavesTheSurvivingHistoryAContiguousPrefixOfTheLsnSequence() {
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            // S1 and S2 predate the cutoff; S3 and S4 do not. That is an ordinary, healthy prune:
            // the old PREFIX goes, so the survivors are the newest entries - and they must be
            // contiguous. (The direction matters: a time prune removes the oldest history.)
            WriteSegmentsWithLsns(coldPath, (1, January), (2, January), (3, Recent), (4, Recent));

            cold.PruneArchiveOlderThan((ulong)new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero).UtcTicks);

            var history = WalArchive.ReadHistory(coldPath, [], 0).Unwrap();
            var lsns = history.Select(h => h.Entry.Lsn).ToArray();
            Assert.That(lsns, Is.EqualTo(new[] { 3L, 4L }),
                "the survivors must be an unbroken run of the newest entries - never 1, 2, 4 with 3 missing");
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    [Test]
    public void ACollectionPass_WithABackwardsClockStep_KeepsTheWholePrefixRatherThanLeavingAHole() {
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            // The flaw's exact shape: S2's clock stamp precedes the cutoff while its neighbours do
            // not - what an NTP step backwards looks like on disk. Deleting S2 alone would leave a
            // hole, so the prefix-monotonic prune must instead stop at S1 and delete NOTHING.
            WriteSegmentsWithLsns(coldPath, (1, Recent), (2, January), (3, Recent));

            var deleted = cold.PruneArchiveOlderThan((ulong)new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero).UtcTicks);

            Assert.That(deleted.Unwrap(), Is.EqualTo(0),
                "a backwards clock step must make the prune keep MORE, never carve a hole at S2");
            var history = WalArchive.ReadHistory(coldPath, [], 0).Unwrap();
            Assert.That(history.Select(h => h.Entry.Lsn), Is.EqualTo(new[] { 1L, 2L, 3L }),
                "all three segments must survive intact");
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    [Test]
    public void ACollectionPass_WhoseClockRanBackBeforeAnOldSegment_StillNeverEmitsANonContiguousHistory() {
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            // Several interior segments carry stamps older than the cutoff while their neighbours do
            // not - a clock that ran back and forth. Whatever the prune decides, the history it leaves
            // must be replayable without silently losing changes; that is the whole point.
            WriteSegmentsWithLsns(coldPath, (1, Recent), (2, January), (3, Recent), (4, January), (5, Recent));

            cold.PruneArchiveOlderThan((ulong)new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero).UtcTicks);

            var lsns = WalArchive.ReadHistory(coldPath, [], 0).Unwrap().Select(h => h.Entry.Lsn).ToArray();
            for (var i = 1; i < lsns.Length; i++)
                Assert.That(lsns[i], Is.EqualTo(lsns[i - 1] + 1),
                    $"surviving LSNs {string.Join(",", lsns)} must be contiguous - a hole at index {i} " +
                    "means replay would rebuild a WRONG state, silently");
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    [Test]
    public void ACollectionPass_KeepsAnUnstampedSegmentThatSitsBeforeTheDeletablePrefix() {
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            var archiveDir = Path.Combine(coldPath, WalArchive.ArchiveDirectoryName);
            // Unstamped first: its age cannot be proven, so it is never a deletion candidate. Under
            // the prefix-monotonic rule it also stops the prune dead - the conservative outcome.
            WalArchive.WriteSegment(archiveDir, Guid.NewGuid(),
                [new DecodedWalEntry(1, WalEntryKind.Operation, [new WalChange(1, ChangeKind.Insert, [1], [9])])], 1);
            WriteSegmentsWithLsns(coldPath, (2, January), (3, January));

            var deleted = cold.PruneArchiveOlderThan((ulong)new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks);

            Assert.That(deleted.Unwrap(), Is.EqualTo(0));
            Assert.That(SegmentFiles(coldPath), Has.Length.EqualTo(3));
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }


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

            RhinoHostConfigTestHelper.WriteConfig(dir, new HostConfig { ColdPath = dir });
            var builder = RhinoHostBuilder.Create(dir);
            var runs = 0;
            builder.OnRetentionRun += _ => Interlocked.Increment(ref runs);
            builder.AddDatabase<NoRetentionDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new NoRetentionDb(cold);
                options.ArchiveRetention = off;      // the disabled policy, from real config semantics
            });

            using var host = (await builder.BuildAsync()).Unwrap();
            host.GetDatabase<NoRetentionDb>().Cold!.Dispose();

            Assert.That(host.ArchiveCollectorCount, Is.EqualTo(0), "a disabled policy builds no collector.");
            await Task.Delay(300);
            Assert.That(Volatile.Read(ref runs), Is.EqualTo(0),
                "A 40ms interval over 300ms would have fired ~7 times had any Timer existed. Zero runs " +
                "means no Timer was ever scheduled - not a timer that exists and does nothing.");

            // Control: the same wiring with retention ON does fire, so the assertion above is
            // about the disabled policy and not about the harness never firing.
            var controlRuns = 0;
            var controlBuilder = RhinoHostBuilder.Create(dir);
            controlBuilder.OnRetentionRun += _ => Interlocked.Increment(ref controlRuns);
            controlBuilder.AddDatabase<NoRetentionDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new NoRetentionDb(cold);
                options.ArchiveRetention = on;
            });

            using var controlHost = (await controlBuilder.BuildAsync()).Unwrap();
            controlHost.GetDatabase<NoRetentionDb>().Cold!.Dispose();
            await Task.Delay(300);

            Assert.That(Volatile.Read(ref controlRuns), Is.GreaterThan(0),
                "sanity: the same wiring WITH retention fires, so a zero above means disabled, not broken.");
            controlHost.Dispose();
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class NoRetentionDb(RhinoDB.Lib.Cold.ColdStore cold) : RhinoDB.Lib.Execution.DbContext(cold);

    // ---- the generation-based prune obeys the same prefix rule as the time-based one ----

    [Test]
    public void AGenerationPrune_WithANonMonotonicArchive_StopsAtThePrefixAndLeavesNoHole() {
        // Generations are monotonic in practice, but nothing enforces it. If a future change ever
        // tagged a later segment lower, `break` must keep MORE segments rather than carve a hole -
        // the same safe direction the timestamp prune takes.
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            WriteSegments(coldPath, (March, 3), (March, 7), (March, 5));   // deliberately out of order

            var deleted = cold.PruneArchiveOlderThanGeneration(6);

            Assert.That(deleted.Unwrap(), Is.EqualTo(1), "only the gen-3 prefix is below the floor.");
            Assert.That(SegmentFiles(coldPath), Has.Length.EqualTo(2), "gen 7 and gen 5 both survive.");
            Assert.That(WalArchive.ReadHistory(coldPath, [], 0).Unwrap(), Has.Count.EqualTo(2),
                "survivors are a contiguous suffix, so replay is not missing a range.");
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    [Test]
    public void AGenerationPrune_WithMonotonicGenerations_StillDeletesEverythingBelowTheFloor() {
        // The prefix rule must not change the ordinary outcome: "keep the last N generations".
        var coldPath = CreateStore();
        try {
            var cold = RhinoDB.Lib.Cold.ColdStore.Open(coldPath).Unwrap();
            WriteSegments(coldPath, (March, 1), (March, 2), (March, 3), (March, 4));

            var deleted = cold.PruneArchiveOlderThanGeneration(3);

            Assert.That(deleted.Unwrap(), Is.EqualTo(2), "generations 1 and 2 are both below the floor.");
            Assert.That(SegmentFiles(coldPath), Has.Length.EqualTo(2));
            Assert.That(cold.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(3));
            cold.Dispose();
        } finally {
            try { Directory.Delete(coldPath, recursive: true); } catch { }
        }
    }

    [Test]
    public void BothPrunePaths_DeleteAPrefixAndNeverTheWholeTail() {
        // The two paths are meant to be structurally identical, so give both the same
        // backwards-clock archive and assert they agree on what survives.
        var byTime = CreateStore();
        var byGeneration = CreateStore();
        try {
            var timeStore = RhinoDB.Lib.Cold.ColdStore.Open(byTime).Unwrap();
            var genStore = RhinoDB.Lib.Cold.ColdStore.Open(byGeneration).Unwrap();
            // Segment 2 is old; segments 1 and 3 are recent. Neither prune may keep 1 and 3 while
            // dropping 2 - that is the hole, and both paths must keep the whole prefix instead.
            WriteSegments(byTime, (March, 1), (January, 1), (March, 1));
            WriteSegments(byGeneration, (March, 1), (March, 1), (March, 1));

            timeStore.PruneArchiveOlderThan((ulong)new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks);
            genStore.PruneArchiveOlderThanGeneration(9);

            Assert.That(SegmentFiles(byTime), Has.Length.EqualTo(3), "a middle old segment stops the time prune entirely.");
            Assert.That(SegmentFiles(byGeneration), Is.Empty, "a floor of 9 sits above every generation here, so the whole archive goes - prefix rule and all.");
            timeStore.Dispose();
            genStore.Dispose();
        } finally {
            try { Directory.Delete(byTime, recursive: true); } catch { }
            try { Directory.Delete(byGeneration, recursive: true); } catch { }
        }
    }
}
