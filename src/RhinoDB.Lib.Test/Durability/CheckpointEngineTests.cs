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
        WriteAheadLog.Create(Path.Combine(dir, "wal.dat"), Guid.NewGuid(), sizeThresholdBytes: long.MaxValue, TimeSpan.FromMinutes(10)).Unwrap();

    private byte[]? ReadRaw(uint dbi, byte[] key) {
        env.BeginTxn(0, out var txn);
        using var tx = txn!;
        var rc = tx.Get(dbi, key, out var value);
        return rc == 0 ? value : null;
    }

    [Test]
    public void ReadCheckpointedLsn_BeforeAnyCheckpoint_ReturnsMinusOne() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);

        var lsn = engine.ReadCheckpointedLsn().Unwrap();

        Assert.That(lsn, Is.EqualTo(-1L));
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
        var wal = WriteAheadLog.Create(path, Guid.NewGuid(), sizeThresholdBytes: long.MaxValue, TimeSpan.FromMinutes(10)).Unwrap();
        await wal.AppendConfirmed(1, WalEntryKind.Operation, new WalChange[] { new(TableId, ChangeKind.Insert, [1], [42]) });
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };

        await engine.RunCheckpoint(1, tableDbis, [new CheckpointRow(TableId, [1], [42])], [], []);
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
    public async Task RunCheckpoint_WhenAPutFails_AbortsWithoutChangingTheWatermarkOrData() {
        using var wal = CreateWal();
        var engine = new CheckpointEngine(env, wal, archiveDir);
        const uint neverOpenedDbi = 999;
        var tableDbis = new Dictionary<uint, uint> { [TableId] = neverOpenedDbi };

        var result = await engine.RunCheckpoint(5, tableDbis, [new CheckpointRow(TableId, [1], [42])], [], []);

        Assert.That(result.IsError(), Is.True);
        Assert.That(engine.ReadCheckpointedLsn().Unwrap(), Is.EqualTo(-1L),
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
    public async Task RunCheckpoint_WithAChangeForAnUnknownTable_RefusesInsteadOfDroppingItAndTruncating() {
        // Schema drift, a foreign WAL file, or a tableId hash collision all look the same here: a
        // tail entry naming a table this database never opened. Skipping it and truncating the
        // WAL would destroy it permanently, so the checkpoint must refuse as a whole.
        var path = Path.Combine(dir, "wal.dat");
        var wal = WriteAheadLog.Create(path, Guid.NewGuid(), sizeThresholdBytes: long.MaxValue, TimeSpan.FromMinutes(10)).Unwrap();
        await wal.AppendConfirmed(1, WalEntryKind.Operation, new WalChange[] { new(TableId, ChangeKind.Insert, [1], [42]) });
        var engine = new CheckpointEngine(env, wal, archiveDir);
        var tableDbis = new Dictionary<uint, uint> { [TableId] = widgetsDbi };
        const uint unknownTableId = 999;

        var result = await engine.RunCheckpoint(1, tableDbis, [new CheckpointRow(TableId, [1], [42]), new CheckpointRow(unknownTableId, [2], [43])], [], []);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(RhinoDB.Core.ErrorKind.SystemFailure));
        Assert.That(engine.ReadCheckpointedLsn().Unwrap(), Is.EqualTo(-1L), "A refused checkpoint must not advance the watermark.");
        Assert.That(ReadRaw(widgetsDbi, [1]), Is.Null, "All-or-nothing: the known table row must not land either.");
        wal.Dispose();
        Assert.That(new FileInfo(path).Length, Is.GreaterThan(WalFileHeaderCodec.Size), "The WAL must not be truncated while it still holds changes we refused to checkpoint.");
    }
}
