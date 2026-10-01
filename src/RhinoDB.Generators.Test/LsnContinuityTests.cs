using System.Reflection;
using RhinoDB.Core;
using RhinoDB.Lib.Changes;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Generators.Test;

// Proves the LSN-ownership refactor's cross-cutting invariants end to end (tasks #75-79, redesigned
// per-table in task #84): the union of every ring-enabled table's OWN ChangeRingBuffer sees every dirty
// transaction - Instant-only included - with a contiguous LSN run across tables, the WAL only ever sees
// Persistent-table transactions (so an Instant-only transaction's LSN is a real, expected, non-corruption
// gap in the WAL - see the fix to WalArchive.MergeInOrder made while writing these tests), and
// RecoveredLsn correctly continues the sequence across a restart rather than colliding with or
// restarting from an LSN the WAL has already recorded.
public class LsnContinuityTests {
    private string dir = "";

    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class GameDb : DbContext<GameDbTransaction> { }

        [Table(TableKind.Instant, typeof(GameDb), RingBufferCapacity = 100)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Widget(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int Value);

        [Table(TableKind.Persistent, typeof(GameDb), RingBufferCapacity = 100)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Club(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int Rating);
        """;

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-lsn-continuity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static private object NewClub(Assembly asm, int id, int rating) =>
        Activator.CreateInstance(asm.GetType("TestNs.Club")!, id, rating)!;

    static private object NewWidget(Assembly asm, int id, int value) =>
        Activator.CreateInstance(asm.GetType("TestNs.Widget")!, id, value)!;

    static private string Camel(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);

    static private ChangeRingBuffer GetRing(object db, string accessor) =>
        (ChangeRingBuffer)db.GetType().GetField($"{Camel(accessor)}Ring", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(db)!;

    static private ulong[] GetAllRingLsns(ChangeRingBuffer ring) {
        using var entries = ring.TryGetChangesSince(0).Unwrap();
        var buffer = entries.Buffer();
        var result = new ulong[buffer.Length];
        for (var i = 0; i < buffer.Length; i++) result[i] = buffer[i].Lsn;
        return result;
    }

    [Test]
    public async Task MixedTransactions_TheUnionOfPerTableRingsHasNoGapsButWalOnlyRecordsThePersistentOnes() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.GameDb")!;
        var txType = asm.GetType("TestNs.GameDbTransaction")!;

        ulong[] ringLsns;
        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var db = Activator.CreateInstance(dbType, cold)!;

            // Persistent (Lsn 0, lands in Club's own ring), Instant-only (Lsn 1, never staged to cold,
            // lands in Widget's own ring), Persistent (Lsn 2, Club's ring again).
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, 80)); return Result.Ok(); },
                PropagationMode.Confirmed);
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)NewWidget(asm, 1, 100)); return Result.Ok(); },
                PropagationMode.Confirmed);
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 2, 75)); return Result.Ok(); },
                PropagationMode.Confirmed);

            ringLsns = GetAllRingLsns(GetRing(db, "Club")).Concat(GetAllRingLsns(GetRing(db, "Widget"))).OrderBy(lsn => lsn).ToArray();
        }

        Assert.That(ringLsns, Is.EqualTo(new ulong[] { 1, 2, 3 }),
            "The union of every table's own ring buffer must cover every dirty transaction, Instant-only included, with no gap in the LSN run - even though each table's changes are now stored separately.");

        // wal.dat is held open (FileShare.None) by the ColdStore that wrote it - reopen fresh to read
        // PendingWalTail as it stands now, matching how a real recovery/replay would observe it.
        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        var walHistory = WalArchive.ReadHistory(reopenedCold.DirectoryPath, reopenedCold.PendingWalTail, reopenedCold.WalGeneration).Unwrap();

        Assert.That(walHistory.Select(e => e.Entry.Lsn), Is.EqualTo(new ulong[] { 1, 3 }),
            "The WAL only ever sees Persistent-table transactions - Lsn 1 (Instant-only) is a real, expected gap, not corruption.");
    }

    [Test]
    public async Task RecoveredLsn_AfterRestart_ContinuesStrictlyAboveTheHighestWalRecordedLsn() {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.GameDb")!;
        var txType = asm.GetType("TestNs.GameDbTransaction")!;

        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var db = Activator.CreateInstance(dbType, cold)!;
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, 80)); return Result.Ok(); },
                PropagationMode.Confirmed);
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 2, 75)); return Result.Ok(); },
                PropagationMode.Confirmed);
        // Club Lsn=1, Club Lsn=2 - both WAL-recorded.
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        Assert.That(reopenedCold.RecoveredLsn, Is.EqualTo(2),
            "RecoveredLsn must reflect the highest LSN that actually has a WAL entry.");

        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
        reopenedCold.CompleteRecovery();

        object? txRef = null;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => { txRef = tx; ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 3, 60)); return Result.Ok(); },
            PropagationMode.Confirmed);
        var lsnAfterRestart = (long)((dynamic)txRef!).LastLsn!;

        Assert.That(lsnAfterRestart, Is.EqualTo(3),
            "The first transaction after reopen must continue the sequence (RecoveredLsn 2 + 1 = 3), not restart from 0 or collide with an LSN already present in the WAL.");
    }

    [Test]
    public async Task RecoveredLsn_AfterRestartWithOnlyInstantOnlyTransactionsBeforeClose_StillContinuesCorrectly() {
        // Regression guard for the WAL-invisible-Instant-only-LSN case specifically: if the LAST
        // transaction before close was Instant-only, no WAL entry records that LSN at all, so
        // RecoveredLsn can only ever reflect the highest WAL-recorded (Persistent) LSN - confirming
        // this doesn't crash or misbehave, and that the next Persistent transaction after reopen still
        // lands strictly above every LSN the WAL has ever recorded.
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.GameDb")!;
        var txType = asm.GetType("TestNs.GameDbTransaction")!;

        using (var cold = ColdStore.Open(dir).Unwrap()) {
            var db = Activator.CreateInstance(dbType, cold)!;
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 1, 80)); return Result.Ok(); },
                PropagationMode.Confirmed);
            await (Task<Result>)GeneratorTestHost.RunTransactional(
                db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)NewWidget(asm, 1, 100)); return Result.Ok(); },
                PropagationMode.Confirmed);
            // Club Lsn=1 (WAL-recorded), Widget Lsn=2 (Instant-only, WAL-invisible) - the session ends
            // right here, on the Instant-only transaction.
        }

        using var reopenedCold = ColdStore.Open(dir).Unwrap();
        Assert.That(reopenedCold.RecoveredLsn, Is.EqualTo(1),
            "RecoveredLsn can only see the highest WAL-recorded LSN (1) - it has no way to know Lsn 2 was already drawn for the Instant-only transaction, since Instant tables don't survive restart at all.");

        var reopenedDb = Activator.CreateInstance(dbType, reopenedCold)!;
        reopenedCold.CompleteRecovery();

        object? txRef = null;
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            reopenedDb, txType, (ctx, tx) => { txRef = tx; ((dynamic)tx).Club.Insert((dynamic)NewClub(asm, 2, 75)); return Result.Ok(); },
            PropagationMode.Confirmed);
        var lsnAfterRestart = (long)((dynamic)txRef!).LastLsn!;

        Assert.That(lsnAfterRestart, Is.EqualTo(2),
            "Harmless reuse, not a collision: Widget's old Lsn=2 never touched the WAL, so a fresh Persistent transaction landing on Lsn=2 again introduces no ambiguity for WAL ordering/dedup.");
    }
}
