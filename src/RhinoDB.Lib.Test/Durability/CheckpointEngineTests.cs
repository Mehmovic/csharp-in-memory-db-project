using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Tables;
using RhinoDB.Native;

namespace RhinoDB.Lib.Durability.Test;

// CheckpointEngine drains the WAL into libmdbx, Docs/05-wal-design.md Phase 3 - real
// libmdbx via the Part A native binding against a real temp directory, no mocking layer
// (matching ColdStoreTests.cs's convention). Torn-WAL-write and mid-file-checksum-corruption
// scenarios are already covered by WalRecordCodecTests.cs (Phase 1) and are not
// re-tested here - CheckpointEngine consumes already-decoded rows/deletes, it never
// scans WAL bytes itself.
public class CheckpointEngineTests {
    private const uint CreateDbi = 0x40000;
    private const ushort DefaultUnixMode = 0b110_100_100;
    private const uint SafeNoSync = 0x10000;
    private const uint TableId = 1;

    private string dir = "";
    private string archiveDir = "";
    private MdbxEnvironment env = null!;
    private uint widgetsDbi;

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-checkpoint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        archiveDir = Path.Combine(dir, WalArchive.ArchiveDirectoryName);

        MdbxEnvironment.Create(out var created);
        env = created!;
        env.SetMaxDbs(16);
        env.SetGeometry(-1, -1, -1, -1, -1, -1);
        env.Open(dir, SafeNoSync, DefaultUnixMode);

        env.BeginTxn(0, out var txn);
        txn!.OpenDbi("widgets", CreateDbi, out widgetsDbi);
        txn.Commit();
    }

    [TearDown]
    public void TearDown() {
        env.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private WriteAheadLog CreateWal() =>
        WriteAheadLog.Create(Path.Combine(dir, "wal.dat"), Guid.NewGuid(), 0, sizeThresholdBytes: long.MaxValue, TimeSpan.FromMinutes(10)).Unwrap();

    private byte[]? ReadRaw(uint dbi, byte[] key) {
        env.BeginTxn(0, out var txn);
        using var tx = txn!;
        var rc = tx.Get(dbi, key, out var value);
        return rc == 0 ? value : null;
    }

    [Test]
    public void ReadCheckpointedLsn_BeforeAnyCheckpoint_ReturnsZero() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        var lsn = engine.ReadCheckpointedLsn().Unwrap();

        Assert.That(lsn, Is.EqualTo(0UL));
    }

    [Test]
    public async Task RunCheckpoint_WithResidentRows_WritesThemAndRecordsTheWatermarkAtomically() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };
        var row = new CheckpointRow(TableId, [1], [42]);

        var result = await engine.RunCheckpoint(10, tableDbis, [row], [], []);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(ReadRaw(widgetsDbi, [1]), Is.EqualTo(new byte[] { 42 }));
        Assert.That(engine.ReadCheckpointedLsn().Unwrap(), Is.EqualTo(10L));
    }

    [Test]
    public async Task RunCheckpoint_ReplaysDeletesSinceLastCheckpoint() {
        env.BeginTxn(0, out var seedTxn);
        seedTxn!.Put(widgetsDbi, [1], [42], 0);
        seedTxn.Commit();

        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };

        var result = await engine.RunCheckpoint(1, tableDbis, [], new[] { (TableId, new byte[] { 1 }) }, []);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(ReadRaw(widgetsDbi, [1]), Is.Null, "A key deleted since the last checkpoint must be gone from mdbx after this one.");
    }

    [Test]
    public async Task RunCheckpoint_TruncatesTheWalAfterCommitting() {
        var path = Path.Combine(dir, "wal.dat");
        var wal = WriteAheadLog.Create(path, Guid.NewGuid(), 0, sizeThresholdBytes: long.MaxValue, TimeSpan.FromMinutes(10)).Unwrap();
        await wal.AppendConfirmed(1, WalEntryKind.Operation, new WalChange[] { new WalChange(TableId, ChangeKind.Insert, [1], [42]) });
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };

        await engine.RunCheckpoint(1, tableDbis, [new CheckpointRow(TableId, [1], [42])], [], new[] { new DecodedWalEntry(1, WalEntryKind.Operation, [new WalChange(TableId, ChangeKind.Insert, [1], [42])]) });
        wal.Dispose();

        Assert.That(new FileInfo(path).Length, Is.EqualTo(WalFileHeaderCodec.Size));
    }

    [Test]
    public async Task RunCheckpoint_ArchivesTheGivenEntriesBeforeTruncating() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };
        var entries = new[] { new DecodedWalEntry(1, WalEntryKind.Operation, [new WalChange(TableId, ChangeKind.Insert, [1], [42])]) };

        var result = await engine.RunCheckpoint(1, tableDbis, [new CheckpointRow(TableId, [1], [42])], [], entries);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(Directory.Exists(archiveDir), Is.True);
        Assert.That(Directory.GetFiles(archiveDir), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task RunCheckpoint_AfterAMigrationBumpedTheGeneration_ArchivesTheSegmentTaggedWithTheCurrentGeneration() {
        // Regression: RunCheckpoint used to hardcode the archived segment's generation to 0 regardless of
        // what WriteGeneration had actually committed - silently mistagging every segment archived after a
        // real migration, which both genesis replay and Phase 6's Prune/MigrateWalArchive depend on being
        // accurate.
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };

        env.BeginTxn(0, out var generationTxn);
        using (generationTxn) {
            engine.WriteGeneration(generationTxn!, 7);
            generationTxn!.Commit();
        }

        var entries = new[] { new DecodedWalEntry(1, WalEntryKind.Operation, [new WalChange(TableId, ChangeKind.Insert, [1], [42])]) };
        var result = await engine.RunCheckpoint(1, tableDbis, [new CheckpointRow(TableId, [1], [42])], [], entries);

        Assert.That(result.IsOk(), Is.True);
        var oldestRetainedGeneration = WalArchive.ReadOldestRetainedGeneration(dir).Unwrap();
        Assert.That(oldestRetainedGeneration.IsSome(), Is.True);
        Assert.That(oldestRetainedGeneration.Get(), Is.EqualTo(7),
            "the archived segment must be tagged with the generation actually committed at checkpoint time, not a hardcoded 0.");
    }

    [Test]
    public async Task RunCheckpoint_WhenAPutFails_AbortsWithoutChangingTheWatermarkOrData() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);
        const uint neverOpenedDbi = 999;
        var tableDbis = new Dictionary<uint, uint> { [TableId] = neverOpenedDbi };

        var result = await engine.RunCheckpoint(5, tableDbis, [new CheckpointRow(TableId, [1], [42])], [], []);

        Assert.That(result.IsError(), Is.True);
        Assert.That(engine.ReadCheckpointedLsn().Unwrap(), Is.EqualTo(0UL),
            "A failed checkpoint must not leave a partially-advanced watermark.");
        Assert.That(ReadRaw(widgetsDbi, [1]), Is.Null, "A failed checkpoint's writes must not land anywhere, including unrelated dbis.");
    }

    [Test]
    public async Task RunCheckpoint_CalledTwiceWithOverlappingData_IsIdempotentAndSafe() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };
        var row = new CheckpointRow(TableId, [1], [42]);

        var first = await engine.RunCheckpoint(10, tableDbis, [row], [], []);
        var second = await engine.RunCheckpoint(10, tableDbis, [row], [], []);

        Assert.That(first.IsOk(), Is.True);
        Assert.That(second.IsOk(), Is.True,
            "A caller retrying a checkpoint after an uncertain crash (succeeded but truncate status unknown) must be able to safely repeat it.");
        Assert.That(ReadRaw(widgetsDbi, [1]), Is.EqualTo(new byte[] { 42 }));
        Assert.That(engine.ReadCheckpointedLsn().Unwrap(), Is.EqualTo(10L));
    }

    [Test]
    public void ReadGeneration_BeforeAnyWrite_ReturnsZero() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        Assert.That(engine.ReadGeneration().Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public void WriteGeneration_ThenRead_RoundTrips() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        env.BeginTxn(0, out var txn);
        using (txn) {
            Assert.That(engine.WriteGeneration(txn!, 7).IsOk(), Is.True);
            txn!.Commit();
        }

        Assert.That(engine.ReadGeneration().Unwrap(), Is.EqualTo(7));
    }

    [Test]
    public void WriteGeneration_TransactionNeverCommitted_LeavesGenerationUnchanged() {
        // Docs/06-schema-migration.md §3: the generation must be written in the SAME transaction as the
        // row rewrites it accompanies - proving the write only takes effect on commit (not merely on the
        // Put call) is what makes that atomicity guarantee real, not just documented intent.
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        env.BeginTxn(0, out var txn);
        using (txn) {
            engine.WriteGeneration(txn!, 7);
            // Deliberately not committed - `using` aborts it on Dispose.
        }

        Assert.That(engine.ReadGeneration().Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public async Task RunCheckpoint_WithAChangeForAnUnknownTable_RefusesInsteadOfDroppingItAndTruncating() {
        // Schema drift, a foreign WAL file, or a tableId hash collision all look the same here: a
        // tail entry naming a table this database never opened. Skipping it and truncating the
        // WAL would destroy it permanently, so the checkpoint must refuse as a whole.
        var path = Path.Combine(dir, "wal.dat");
        var wal = WriteAheadLog.Create(path, Guid.NewGuid(), 0, sizeThresholdBytes: long.MaxValue, TimeSpan.FromMinutes(10)).Unwrap();
        await wal.AppendConfirmed(1, WalEntryKind.Operation, new WalChange[] { new WalChange(TableId, ChangeKind.Insert, [1], [42]) });
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };
        const uint unknownTableId = 999;

        var result = await engine.RunCheckpoint(1, tableDbis, [new CheckpointRow(TableId, [1], [42]), new CheckpointRow(unknownTableId, [2], [43])], [], []);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(RhinoDB.Core.ErrorKind.SystemFailure));
        Assert.That(engine.ReadCheckpointedLsn().Unwrap(), Is.EqualTo(0UL), "A refused checkpoint must not advance the watermark.");
        Assert.That(ReadRaw(widgetsDbi, [1]), Is.Null, "All-or-nothing: the known table row must not land either.");
        wal.Dispose();
        Assert.That(new FileInfo(path).Length, Is.GreaterThan(WalFileHeaderCodec.Size), "The WAL must not be truncated while it still holds changes we refused to checkpoint.");
    }

    [Test]
    public void ReadRetainedFromGeneration_BeforeAnyWrite_ReturnsZero() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        Assert.That(engine.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public void WriteRetainedFromGeneration_ThenRead_RoundTrips() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        env.BeginTxn(0, out var txn);
        using (txn) {
            Assert.That(engine.WriteRetainedFromGeneration(txn!, 3).IsOk(), Is.True);
            txn!.Commit();
        }

        Assert.That(engine.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(3));
    }

    [Test]
    public void WriteRetainedFromGeneration_TransactionNeverCommitted_LeavesItUnchanged() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        env.BeginTxn(0, out var txn);
        using (txn) {
            engine.WriteRetainedFromGeneration(txn!, 3);
            // Deliberately not committed - `using` aborts it on Dispose.
        }

        Assert.That(engine.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(0));
    }

    [Test]
    public void ReadOnAFreshInstance_ThenWrite_StillWorks() {
        // Regression: a read-only call's transaction is always aborted (never committed), and libmdbx
        // rolls back a dbi CREATE on abort. If EnsureMetadataDbi's read path opened the dbi with the CREATE
        // flag (as it originally did, shared with the write path), the FIRST-EVER read on a truly fresh
        // store - one that's never had anything written to __rhinodb_checkpoint__ - would cache a dbi
        // handle that the abort had just invalidated, and every later WRITE reusing that cached handle
        // (same CheckpointEngine instance) would fail with MDBX_BAD_DBI. RhinoRunMode.WalPrune's backward-guard
        // read (ReadRetainedFromGeneration) followed by its own write is exactly this sequence on a
        // never-checkpointed database.
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        Assert.That(engine.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(0), "read first, on a truly fresh store.");

        env.BeginTxn(0, out var txn);
        using (txn) {
            Assert.That(engine.WriteRetainedFromGeneration(txn!, 4).IsOk(), Is.True);
            txn!.Commit();
        }

        Assert.That(engine.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(4));
    }

    [Test]
    public void WriteRetainedFromGeneration_IsIndependentOfGeneration() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        env.BeginTxn(0, out var txn);
        using (txn) {
            engine.WriteGeneration(txn!, 9);
            engine.WriteRetainedFromGeneration(txn!, 3);
            txn!.Commit();
        }

        Assert.That(engine.ReadGeneration().Unwrap(), Is.EqualTo(9));
        Assert.That(engine.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(3));
    }

    // ---- archiving must succeed before the WAL is allowed to be destroyed ----

    [Test]
    public async Task RunCheckpoint_WhenArchivingFails_ReturnsTheErrorAndLeavesTheWalIntact() {
        var path = Path.Combine(dir, "wal.dat");
        var wal = WriteAheadLog.Create(path, Guid.NewGuid(), 0, sizeThresholdBytes: long.MaxValue, TimeSpan.FromMinutes(10)).Unwrap();
        await wal.AppendConfirmed(1, WalEntryKind.Operation, new WalChange[] { new WalChange(TableId, ChangeKind.Insert, [1], [42]) });

        // Make the archive directory un-creatable: a plain FILE sits where it needs to go, so
        // WriteSegment's Directory.CreateDirectory throws and the archive write fails.
        File.WriteAllText(archiveDir, "not a directory");
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };

        var result = await engine.RunCheckpoint(1, tableDbis, [new CheckpointRow(TableId, [1], [42])], [], new[] { new DecodedWalEntry(1, WalEntryKind.Operation, [new WalChange(TableId, ChangeKind.Insert, [1], [42])]) });
        var lengthBeforeDispose = new FileInfo(path).Length;
        wal.Dispose();

        Assert.That(result.IsError(), Is.True, "a failed archive must not be swallowed");
        Assert.That(lengthBeforeDispose, Is.GreaterThan(WalFileHeaderCodec.Size),
            "The WAL holds the only remaining copy of this generation's replay history, and the " +
            "watermark now sits past those LSNs - truncating here would destroy it permanently.");
    }

    [Test]
    public async Task RunCheckpoint_WhenArchivingFails_StillCommitsTheRowsSoNoDataIsLost() {
        var wal = CreateWal();
        await wal.AppendConfirmed(1, WalEntryKind.Operation, new WalChange[] { new WalChange(TableId, ChangeKind.Insert, [1], [42]) });
        File.WriteAllText(archiveDir, "not a directory");
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };

        await engine.RunCheckpoint(1, tableDbis, [new CheckpointRow(TableId, [1], [42])], [], new[] { new DecodedWalEntry(1, WalEntryKind.Operation, [new WalChange(TableId, ChangeKind.Insert, [1], [42])]) });

        Assert.That(ReadRaw(widgetsDbi, [1]), Is.EqualTo(new byte[] { 42 }),
            "Refusing to truncate is about HISTORY, not data - the rows committed to mdbx above stand.");
    }
}
