using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Lib.Durability.Test;

// WriteAheadLog is the group commit engine, Docs/05-wal-design.md Phase 2 - real file I/O
// against a real temp directory, no mocking layer (matching ColdStoreTests.cs's and
// DirectorySyncTests.cs's established convention in this repo). No Task.Delay/window exists
// by design, so "two Confirmed calls share one flush" can't be proven by timing alone -
// TestOnlyBeforeFlush is the deterministic seam these tests use to hold a flush open long
// enough to observe a second concurrent append actually join it.
public class WriteAheadLogTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-wal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private WriteAheadLog CreateWal(long sizeThresholdBytes = long.MaxValue, TimeSpan periodicFlushInterval = default) =>
        WriteAheadLog.Create(
            Path.Combine(dir, "wal.dat"),
            Guid.NewGuid(),
            sizeThresholdBytes,
            periodicFlushInterval == default ? TimeSpan.FromMinutes(10) : periodicFlushInterval
        ).Unwrap();

    static private WalChange[] OneChange(long key = 1) => [new WalChange(1, ChangeKind.Insert, BitConverter.GetBytes(key), [9])];

    [Test]
    public void Create_AgainstAFreshPath_WritesAValidHeaderImmediately() {
        var path = Path.Combine(dir, "wal.dat");
        var databaseId = Guid.NewGuid();

        WriteAheadLog.Create(path, databaseId).Unwrap().Dispose();

        var bytes = File.ReadAllBytes(path);
        var ok = WalFileHeaderCodec.TryDecode(bytes, out var header);
        Assert.That(ok, Is.True);
        Assert.That(header.DatabaseId, Is.EqualTo(databaseId));
    }

    [Test]
    public void Create_AgainstAPathThatAlreadyExists_Fails() {
        var path = Path.Combine(dir, "wal.dat");
        using var first = WriteAheadLog.Create(path, Guid.NewGuid()).Unwrap();

        var second = WriteAheadLog.Create(path, Guid.NewGuid());

        Assert.That(second.IsError(), Is.True);
    }

    [Test]
    public async Task AppendConfirmed_ThenAwaited_PersistsTheEntryDurably() {
        var path = Path.Combine(dir, "wal.dat");
        using (var wal = WriteAheadLog.Create(path, Guid.NewGuid()).Unwrap()) {
            var error = await wal.AppendConfirmed(7, WalEntryKind.Operation, OneChange(42));
            Assert.That(error, Is.Null);
        }

        var bytes = File.ReadAllBytes(path);
        var afterHeader = bytes.AsSpan(WalFileHeaderCodec.Size).ToArray();
        var scan = WalRecordCodec.Scan(afterHeader);

        Assert.That(scan.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(scan.Entries, Has.Count.EqualTo(1));
        Assert.That(scan.Entries[0].Lsn, Is.EqualTo(7));
        Assert.That(scan.Entries[0].Changes[0].Key, Is.EqualTo(BitConverter.GetBytes(42L)));
    }

    [Test]
    public void AppendConfirmed_TwoCallsArrivingWhileAGroupIsInFlight_ShareOneCompletion() {
        using var wal = CreateWal();
        using var flushGate = new ManualResetEventSlim(false);
        using var flushEntered = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => { flushEntered.Set(); flushGate.Wait(); };

        var task1 = wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange());
        Assert.That(flushEntered.Wait(TimeSpan.FromSeconds(5)), Is.True, "RunFlush never started.");

        var task2 = wal.AppendConfirmed(2, WalEntryKind.Operation, OneChange());

        Assert.That(task2, Is.SameAs(task1),
            "Two Confirmed calls arriving while a group is in flight should share exactly one physical flush.");

        flushGate.Set();
        Assert.That(task1.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(task1.Result, Is.Null);
    }

    [Test]
    public async Task AppendConfirmed_ACallArrivingAfterThePriorGroupAlreadyCompleted_GetsItsOwn() {
        using var wal = CreateWal();

        var task1 = wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange());
        await task1;

        var task2 = wal.AppendConfirmed(2, WalEntryKind.Operation, OneChange());

        Assert.That(task2, Is.Not.SameAs(task1),
            "A Confirmed call arriving after the prior group already completed must get its own flush, not join a stale one.");
        Assert.That(await task2, Is.Null);
    }

    [Test]
    public void AppendOptimistic_ReturnsImmediatelyWithoutWaitingOnAFlush() {
        using var wal = CreateWal();
        using var flushGate = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushGate.Wait(TimeSpan.FromSeconds(5));

        Assert.DoesNotThrow(() => wal.AppendOptimistic(1, WalEntryKind.Operation, OneChange()));

        flushGate.Set();
    }

    [Test]
    public void AppendOptimistic_CrossingTheSizeThresholdWithoutAnyConfirmedCall_TriggersAFlush() {
        using var wal = CreateWal(sizeThresholdBytes: 1);
        using var flushEntered = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushEntered.Set();

        wal.AppendOptimistic(1, WalEntryKind.Operation, OneChange());

        Assert.That(flushEntered.Wait(TimeSpan.FromSeconds(5)), Is.True,
            "An Optimistic append that crosses the size threshold must trigger its own flush, with no Confirmed call involved.");
    }

    [Test]
    public void AppendOptimistic_WithNoTriggerCrossedYet_NeverFlushesOnItsOwn() {
        using var wal = CreateWal(sizeThresholdBytes: long.MaxValue);
        using var flushEntered = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushEntered.Set();

        wal.AppendOptimistic(1, WalEntryKind.Operation, OneChange());

        Assert.That(flushEntered.Wait(TimeSpan.FromMilliseconds(300)), Is.False,
            "Without a Confirmed arrival, a crossed size threshold, or a periodic tick, an Optimistic append must not force a flush.");
    }

    [Test]
    public void PeriodicTick_WithPendingUnflushedBytes_EventuallyTriggersAFlush() {
        using var wal = CreateWal(sizeThresholdBytes: long.MaxValue, periodicFlushInterval: TimeSpan.FromMilliseconds(20));
        using var flushEntered = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushEntered.Set();

        wal.AppendOptimistic(1, WalEntryKind.Operation, OneChange());

        Assert.That(flushEntered.Wait(TimeSpan.FromSeconds(2)), Is.True,
            "The periodic tick must eventually flush a pending Optimistic append with no Confirmed call and no threshold crossing.");
    }

    [Test]
    public void PeriodicTick_WithNothingPending_NeverFlushes() {
        using var wal = CreateWal(sizeThresholdBytes: long.MaxValue, periodicFlushInterval: TimeSpan.FromMilliseconds(20));
        using var flushEntered = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushEntered.Set();

        Assert.That(flushEntered.Wait(TimeSpan.FromMilliseconds(200)), Is.False,
            "The periodic tick must not fire a flush when there is nothing unflushed to flush.");
    }

    [Test]
    public void Dispose_FlushesAnyOutstandingAppendsBeforeClosing() {
        var path = Path.Combine(dir, "wal.dat");
        var wal = WriteAheadLog.Create(path, Guid.NewGuid()).Unwrap();
        wal.AppendOptimistic(3, WalEntryKind.Operation, OneChange());

        wal.Dispose();

        var bytes = File.ReadAllBytes(path);
        var scan = WalRecordCodec.Scan(bytes.AsSpan(WalFileHeaderCodec.Size).ToArray());
        Assert.That(scan.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(scan.Entries, Has.Count.EqualTo(1));
    }
}
