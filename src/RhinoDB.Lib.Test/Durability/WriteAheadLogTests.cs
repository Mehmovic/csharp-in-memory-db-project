using System.Reflection;

using RhinoDB.Core;
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
            0,
            sizeThresholdBytes,
            periodicFlushInterval == default ? TimeSpan.FromMinutes(10) : periodicFlushInterval
        ).Unwrap();

    static private WalChange[] OneChange(long key = 1) => [new WalChange(1, ChangeKind.Insert, BitConverter.GetBytes(key), [9])];

    [Test]
    public void Create_AgainstAFreshPath_WritesAValidHeaderImmediately() {
        var path = Path.Combine(dir, "wal.dat");
        var databaseId = Guid.NewGuid();

        WriteAheadLog.Create(path, databaseId, 0).Unwrap().Dispose();

        var bytes = File.ReadAllBytes(path);
        var ok = WalFileHeaderCodec.TryDecode(bytes, out var header);
        Assert.That(ok, Is.True);
        Assert.That(header.DatabaseId, Is.EqualTo(databaseId));
        Assert.That(header.Generation, Is.EqualTo(0), "Generation defaults to 0 until the schema-migration engine assigns a real value.");
    }

    [Test]
    public void Create_WithAnExplicitGeneration_RoundTripsThroughOpen() {
        var path = Path.Combine(dir, "wal.dat");

        using (var wal = WriteAheadLog.Create(path, Guid.NewGuid(), generation: 7).Unwrap()) {
            Assert.That(wal.Generation, Is.EqualTo(7u));
        }

        var opened = WriteAheadLog.Open(path).Unwrap();
        Assert.That(opened.Wal.Generation, Is.EqualTo(7u));
        opened.Wal.Dispose();
    }

    [Test]
    public void Create_AgainstAPathThatAlreadyExists_Fails() {
        var path = Path.Combine(dir, "wal.dat");
        using var first = WriteAheadLog.Create(path, Guid.NewGuid(), 0).Unwrap();

        var second = WriteAheadLog.Create(path, Guid.NewGuid(), 0);

        Assert.That(second.IsError(), Is.True);
    }

    [Test]
    public async Task AppendConfirmed_ThenAwaited_PersistsTheEntryDurably() {
        var path = Path.Combine(dir, "wal.dat");
        using (var wal = WriteAheadLog.Create(path, Guid.NewGuid(), 0).Unwrap()) {
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

#if DEBUG
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
#endif

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

#if DEBUG
    [Test]
    public void AppendOptimistic_ReturnsImmediatelyWithoutWaitingOnAFlush() {
        using var wal = CreateWal();
        using var flushGate = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushGate.Wait(TimeSpan.FromSeconds(5));

        Assert.DoesNotThrow(() => wal.AppendOptimistic(1, WalEntryKind.Operation, OneChange()));

        flushGate.Set();
    }
#endif

#if DEBUG
    [Test]
    public void AppendOptimistic_CrossingTheSizeThresholdWithoutAnyConfirmedCall_TriggersAFlush() {
        using var wal = CreateWal(sizeThresholdBytes: 1);
        using var flushEntered = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushEntered.Set();

        wal.AppendOptimistic(1, WalEntryKind.Operation, OneChange());

        Assert.That(flushEntered.Wait(TimeSpan.FromSeconds(5)), Is.True,
            "An Optimistic append that crosses the size threshold must trigger its own flush, with no Confirmed call involved.");
    }
#endif

#if DEBUG
    [Test]
    public void AppendOptimistic_WithNoTriggerCrossedYet_NeverFlushesOnItsOwn() {
        using var wal = CreateWal(sizeThresholdBytes: long.MaxValue);
        using var flushEntered = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushEntered.Set();

        wal.AppendOptimistic(1, WalEntryKind.Operation, OneChange());

        Assert.That(flushEntered.Wait(TimeSpan.FromMilliseconds(300)), Is.False,
            "Without a Confirmed arrival, a crossed size threshold, or a periodic tick, an Optimistic append must not force a flush.");
    }
#endif

#if DEBUG
    [Test]
    public void PeriodicTick_WithPendingUnflushedBytes_EventuallyTriggersAFlush() {
        using var wal = CreateWal(sizeThresholdBytes: long.MaxValue, periodicFlushInterval: TimeSpan.FromMilliseconds(20));
        using var flushEntered = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushEntered.Set();

        wal.AppendOptimistic(1, WalEntryKind.Operation, OneChange());

        Assert.That(flushEntered.Wait(TimeSpan.FromSeconds(2)), Is.True,
            "The periodic tick must eventually flush a pending Optimistic append with no Confirmed call and no threshold crossing.");
    }
#endif

#if DEBUG
    [Test]
    public void PeriodicTick_WithNothingPending_NeverFlushes() {
        using var wal = CreateWal(sizeThresholdBytes: long.MaxValue, periodicFlushInterval: TimeSpan.FromMilliseconds(20));
        using var flushEntered = new ManualResetEventSlim(false);
        wal.TestOnlyBeforeFlush = () => flushEntered.Set();

        Assert.That(flushEntered.Wait(TimeSpan.FromMilliseconds(200)), Is.False,
            "The periodic tick must not fire a flush when there is nothing unflushed to flush.");
    }
#endif

    [Test]
    public void Dispose_FlushesAnyOutstandingAppendsBeforeClosing() {
        var path = Path.Combine(dir, "wal.dat");
        var wal = WriteAheadLog.Create(path, Guid.NewGuid(), 0).Unwrap();
        wal.AppendOptimistic(3, WalEntryKind.Operation, OneChange());

        wal.Dispose();

        var bytes = File.ReadAllBytes(path);
        var scan = WalRecordCodec.Scan(bytes.AsSpan(WalFileHeaderCodec.Size).ToArray());
        Assert.That(scan.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(scan.Entries, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Truncate_ThenAppend_WritesRightAfterTheHeaderNotAtTheStalePosition() {
        var path = Path.Combine(dir, "wal.dat");
        var wal = WriteAheadLog.Create(path, Guid.NewGuid(), 0).Unwrap();
        var error = await wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange());
        Assert.That(error, Is.Null);

        var truncateError = await wal.Truncate();
        Assert.That(truncateError, Is.Null);

        var appendError = await wal.AppendConfirmed(2, WalEntryKind.Operation, OneChange(2));
        Assert.That(appendError, Is.Null);
        wal.Dispose();

        var bytes = File.ReadAllBytes(path);
        var expectedFrame = WalRecordCodec.Encode(2, WalEntryKind.Operation, OneChange(2));
        Assert.That(bytes.Length, Is.EqualTo(WalFileHeaderCodec.Size + expectedFrame.Length),
            "The post-truncate append must land immediately after the header, not at the stale pre-truncate file position.");
        var scan = WalRecordCodec.Scan(bytes.AsSpan(WalFileHeaderCodec.Size).ToArray());
        Assert.That(scan.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(scan.Entries, Has.Count.EqualTo(1));
        Assert.That(scan.Entries[0].Lsn, Is.EqualTo(2));
    }

    [Test]
    public async Task Open_AgainstAPreviouslyClosedWal_RecoversAllPreviouslyDurableEntries() {
        var path = Path.Combine(dir, "wal.dat");
        var databaseId = Guid.NewGuid();
        using (var wal = WriteAheadLog.Create(path, databaseId, 0).Unwrap()) {
            await wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange());
            await wal.AppendConfirmed(2, WalEntryKind.Operation, OneChange(2));
        }

        var opened = WriteAheadLog.Open(path).Unwrap();
        using var reopenedWal = opened.Wal;

        Assert.That(opened.Entries.Select(e => e.Lsn), Is.EquivalentTo(new ulong[] { 1, 2 }));

        var error = await reopenedWal.AppendConfirmed(3, WalEntryKind.Operation, OneChange(3));
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Open_WithATornTailEntry_TruncatesToTheLastGoodEntryAndStillOpens() {
        var path = Path.Combine(dir, "wal.dat");
        using (var wal = WriteAheadLog.Create(path, Guid.NewGuid(), 0).Unwrap()) {
            wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange()).GetAwaiter().GetResult();
        }
        using (var fs = new FileStream(path, FileMode.Append)) fs.Write([1, 2, 3]); // simulate a torn/partial trailing write

        var opened = WriteAheadLog.Open(path).Unwrap();
        using var reopenedWal = opened.Wal;

        Assert.That(opened.Entries.Select(e => e.Lsn), Is.EquivalentTo(new ulong[] { 1 }));
    }

    [Test]
    public void Open_WithMidFileCorruption_RefusesToOpen() {
        var path = Path.Combine(dir, "wal.dat");
        using (var wal = WriteAheadLog.Create(path, Guid.NewGuid(), 0).Unwrap()) {
            wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange()).GetAwaiter().GetResult();
            wal.AppendConfirmed(2, WalEntryKind.Operation, OneChange(2)).GetAwaiter().GetResult();
        }
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
            // Corrupt a byte inside the first entry's PAYLOAD (after its fixed header, so the
            // stored length still matches what's actually there) - corrupting the length field
            // itself would just look like a torn tail, not the mid-file corruption this test wants.
            fs.Seek(WalFileHeaderCodec.Size + WalRecordCodec.HeaderSize + 1, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
        }

        var opened = WriteAheadLog.Open(path);

        Assert.That(opened.IsError(), Is.True);
    }

    [Test]
    public async Task AppendConfirmed_ManyConcurrentAppendsUnderLoad_EveryAcknowledgedEntryIsActuallyPersisted() {
        const int OperationCount = 5_000;
        var path = Path.Combine(dir, "wal.dat");
        var wal = WriteAheadLog.Create(path, Guid.NewGuid(), 0).Unwrap();

        var pending = new Task<DbError?>[OperationCount];
        for (var i = 0; i < OperationCount; i++) {
            var lsn = (ulong)(i + 1);
            pending[i] = wal.AppendConfirmed(lsn, WalEntryKind.Operation, OneChange((long)lsn));
        }
        var results = await Task.WhenAll(pending);
        wal.Dispose();

        Assert.That(results, Has.All.Null, "Every append this loop issued must come back acknowledged with no error.");

        var bytes = File.ReadAllBytes(path);
        var scan = WalRecordCodec.Scan(bytes.AsSpan(WalFileHeaderCodec.Size).ToArray());
        Assert.That(scan.Status, Is.EqualTo(WalScanStatus.Clean));
        Assert.That(scan.Entries, Has.Count.EqualTo(OperationCount),
            "Every entry acknowledged as durable must actually be present on disk - a late joiner incorrectly sharing an already-flushed group's completion would silently lose entries here.");
        Assert.That(scan.Entries.Select(e => e.Lsn), Is.EquivalentTo(Enumerable.Range(1, OperationCount).Select(i => (long)i)));
    }

#if DEBUG
    // ---- Two-stage group commit: the fsync runs outside the append lock ----
    // TestOnlyDuringFsync runs after a group is cut, outside the lock, where the fsync happens. Set by reflection so
    // this file compiles before it exists.

    static private void SetDuringFsync(WriteAheadLog wal, Action? hook) {
        var property = typeof(WriteAheadLog).GetProperty("TestOnlyDuringFsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(property, Is.Not.Null, "WriteAheadLog.TestOnlyDuringFsync doesn't exist yet.");
        property!.SetValue(wal, hook);
    }

    [Test]
    public async Task AnAppendDuringAnFsync_IsNotBlockedByIt() {
        using var wal = CreateWal();
        using var fsyncGate = new ManualResetEventSlim(false);
        using var fsyncEntered = new ManualResetEventSlim(false);
        SetDuringFsync(wal, () => { fsyncEntered.Set(); fsyncGate.Wait(TimeSpan.FromSeconds(10)); });
        try {
            var first = wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange());
            Assert.That(fsyncEntered.Wait(TimeSpan.FromSeconds(5)), Is.True, "the first group's fsync never started.");

            var optimistic = Task.Run(() => wal.AppendOptimistic(2, WalEntryKind.Operation, OneChange(2)));
            var confirmedCall = Task.Factory.StartNew(() => wal.AppendConfirmed(3, WalEntryKind.Operation, OneChange(3)));

            Assert.That(optimistic.Wait(TimeSpan.FromSeconds(2)), Is.True, "an append must not wait for another group's fsync.");
            Assert.That(confirmedCall.Wait(TimeSpan.FromSeconds(2)), Is.True, "the Confirmed call itself returns at once - only its task waits.");

            fsyncGate.Set();
            Assert.That(await first, Is.Null);
            Assert.That(await confirmedCall.Result, Is.Null);
        } finally {
            fsyncGate.Set();
            SetDuringFsync(wal, null);
        }
    }

    [Test]
    public async Task AConfirmedAppendMadeDuringAnFsync_IsCoveredByALaterFsync_NotThatOne() {
        using var wal = CreateWal();
        using var fsyncGate = new ManualResetEventSlim(false);
        using var fsyncEntered = new ManualResetEventSlim(false);
        var fsyncs = 0;
        SetDuringFsync(wal, () => {
            if (Interlocked.Increment(ref fsyncs) != 1) return;
            fsyncEntered.Set();
            fsyncGate.Wait(TimeSpan.FromSeconds(10));
        });
        try {
            var first = wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange());
            Assert.That(fsyncEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);

            var second = wal.AppendConfirmed(2, WalEntryKind.Operation, OneChange(2));
            var fsyncsWhenSecondCompleted = second.ContinueWith(_ => Volatile.Read(ref fsyncs), TaskContinuationOptions.ExecuteSynchronously);

            Assert.That(second, Is.Not.SameAs(first), "its bytes may not be in the fsync already running - it belongs to the next group.");
            fsyncGate.Set();
            Assert.That(await first, Is.Null);
            Assert.That(await second, Is.Null);
            Assert.That(await fsyncsWhenSecondCompleted, Is.GreaterThanOrEqualTo(2), "reported durable only by an fsync that started after it was written.");
        } finally {
            fsyncGate.Set();
            SetDuringFsync(wal, null);
        }
    }

    [Test]
    public async Task ConfirmedAppendsMadeDuringAnFsync_ShareTheNextOne() {
        using var wal = CreateWal();
        using var fsyncGate = new ManualResetEventSlim(false);
        using var fsyncEntered = new ManualResetEventSlim(false);
        var fsyncs = 0;
        SetDuringFsync(wal, () => {
            if (Interlocked.Increment(ref fsyncs) != 1) return;
            fsyncEntered.Set();
            fsyncGate.Wait(TimeSpan.FromSeconds(10));
        });
        try {
            var first = wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange());
            Assert.That(fsyncEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);

            var waiting = Enumerable.Range(2, 9).Select(i => wal.AppendConfirmed((ulong)i, WalEntryKind.Operation, OneChange(i))).ToArray();

            Assert.That(waiting.Distinct().Count(), Is.EqualTo(1), "everything written during one fsync is batched into the next.");
            fsyncGate.Set();
            Assert.That(await first, Is.Null);
            Assert.That(await waiting[0], Is.Null);
            Assert.That(Volatile.Read(ref fsyncs), Is.EqualTo(2), "two groups, two fsyncs - not one per append.");
        } finally {
            fsyncGate.Set();
            SetDuringFsync(wal, null);
        }
    }

    [Test]
    public async Task AFailedFsync_IsSticky_EveryLaterGroupFailsToo() {
        // A failed fsync may have dropped dirty pages: a later "successful" fsync must not vouch for entries written
        // after bytes that may be gone.
        using var wal = CreateWal();
        using var fsyncGate = new ManualResetEventSlim(false);
        using var fsyncEntered = new ManualResetEventSlim(false);
        var fsyncs = 0;
        SetDuringFsync(wal, () => {
            if (Interlocked.Increment(ref fsyncs) != 1) return;
            fsyncEntered.Set();
            fsyncGate.Wait(TimeSpan.FromSeconds(10));
            throw new IOException("injected fsync failure");
        });
        try {
            var first = wal.AppendConfirmed(1, WalEntryKind.Operation, OneChange());
            Assert.That(fsyncEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            var second = wal.AppendConfirmed(2, WalEntryKind.Operation, OneChange(2));
            fsyncGate.Set();

            Assert.That((await first)?.Kind, Is.EqualTo(ErrorKind.WalDurabilityFailed));
            Assert.That((await second)?.Kind, Is.EqualTo(ErrorKind.WalDurabilityFailed), "the group after a failed fsync fails too.");
            Assert.That((await wal.AppendConfirmed(3, WalEntryKind.Operation, OneChange(3)))?.Kind, Is.EqualTo(ErrorKind.WalDurabilityFailed),
                "and so does every later Confirmed append.");
        } finally {
            fsyncGate.Set();
            SetDuringFsync(wal, null);
        }
    }
#endif
}
