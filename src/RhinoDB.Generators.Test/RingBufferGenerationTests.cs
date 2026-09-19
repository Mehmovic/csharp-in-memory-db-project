using System.Reflection;
using RhinoDB.Core;
using RhinoDB.Lib.Changes;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Test.Generators;

// Proves EmitRingBufferRecordCall's wiring (task #79): Insert/Update/Delete on both an Instant and a
// Persistent table push a matching entry into the per-Db ChangeRingBuffer (correct TableId/ChangeKind/
// key/row, strictly increasing Lsn shared across both kinds), and RingBuffer=false opts a table out of
// ring participation entirely.
public class RingBufferGenerationTests {
    private string dir = "";

    private const string Source = """
        using MemoryPack;
        using MessagePack;
        using RhinoDB.Core.Tables;
        using RhinoDB.Lib.Execution;

        namespace TestNs;

        [Database]
        public partial class RingDb : DbContext<RingDbTransaction> { }

        [Table(TableKind.Instant, typeof(RingDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Widget(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] int Value);

        [Table(TableKind.Persistent, typeof(RingDb))]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Account(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id,
            [property: MemoryPackOrder(1)] [property: Key(1)] decimal Balance);

        [Table(TableKind.Instant, typeof(RingDb), RingBuffer = false)]
        [MemoryPackable(GenerateType.VersionTolerant)]
        [MessagePackObject]
        public readonly partial record struct Muted(
            [PrimaryKey] [property: MemoryPackOrder(0)] [property: Key(0)] int Id);
        """;

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-ring-buffer-generation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static private (object Db, Type TxType, Assembly Assembly) NewDb(ColdStore cold) {
        var (asm, _) = GeneratorTestHost.CompileAndLoad(Source);
        var dbType = asm.GetType("TestNs.RingDb")!;
        var txType = asm.GetType("TestNs.RingDbTransaction")!;
        var db = Activator.CreateInstance(dbType, cold)!;
        return (db, txType, asm);
    }

    static private object NewWidget(Assembly asm, int id, int value) =>
        Activator.CreateInstance(asm.GetType("TestNs.Widget")!, id, value)!;

    static private object NewAccount(Assembly asm, int id, decimal balance) =>
        Activator.CreateInstance(asm.GetType("TestNs.Account")!, id, balance)!;

    static private object NewMuted(Assembly asm, int id) =>
        Activator.CreateInstance(asm.GetType("TestNs.Muted")!, id)!;

    static private ChangeRingBuffer GetRing(object db) =>
        (ChangeRingBuffer)db.GetType().GetField("ring", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(db)!;

    static private uint GetTableId(Assembly asm, string opsTypeName) =>
        (uint)asm.GetType(opsTypeName)!.GetField("TableId", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    // Cursor -1, not 0: RingDb has a Persistent table, and ColdStore.RecoveredLsn seeds at -1 for a
    // fresh store (nothing checkpointed yet - see CheckpointEngine.ReadCheckpointedLsn's -1 sentinel),
    // so the shared LsnSequence's first real value is 0, not 1. A cursor of 0 would incorrectly read
    // as "already caught up to LSN 0" and silently exclude that first entry.
    static private RingEntry[] GetAllEntries(ChangeRingBuffer ring) {
        using var entries = ring.TryGetChangesSince(-1).Unwrap();
        return entries.Buffer().ToArray();
    }

    [Test]
    public async Task Insert_OnAnInstantTable_RecordsAMatchingRingEntry() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)NewWidget(asm, 1, 100)); return Result.Ok(); },
            PropagationMode.Confirmed);

        var entries = GetAllEntries(GetRing(db));

        Assert.That(entries, Has.Length.EqualTo(1));
        Assert.That(entries[0].Change.TableId, Is.EqualTo(GetTableId(asm, "TestNs.RingDbWidgetOps")));
        Assert.That(entries[0].Change.Kind, Is.EqualTo(ChangeKind.Insert));
        Assert.That(entries[0].Change.Row, Is.Not.Null);
        Assert.That(entries[0].Lsn, Is.EqualTo(0), "RingDb has a Persistent table, so ColdStore.RecoveredLsn seeds the shared LsnSequence at -1 for a fresh store - the first drawn value is 0, not 1.");
    }

    [Test]
    public async Task Update_OnAnInstantTable_RecordsAMatchingRingEntry() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)NewWidget(asm, 1, 100)); return Result.Ok(); },
            PropagationMode.Confirmed);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Update(1, (dynamic)NewWidget(asm, 1, 500)); return Result.Ok(); },
            PropagationMode.Confirmed);

        var entries = GetAllEntries(GetRing(db));

        Assert.That(entries, Has.Length.EqualTo(2));
        Assert.That(entries[1].Change.Kind, Is.EqualTo(ChangeKind.Update));
        Assert.That(entries[1].Change.Row, Is.Not.Null);
        Assert.That(entries[1].Lsn, Is.EqualTo(1), "LSN must strictly increase transaction over transaction.");
    }

    [Test]
    public async Task Delete_OnAnInstantTable_RecordsARingEntryWithNoRowBytes() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)NewWidget(asm, 1, 100)); return Result.Ok(); },
            PropagationMode.Confirmed);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Delete(1); return Result.Ok(); },
            PropagationMode.Confirmed);

        var entries = GetAllEntries(GetRing(db));

        Assert.That(entries, Has.Length.EqualTo(2));
        Assert.That(entries[1].Change.Kind, Is.EqualTo(ChangeKind.Delete));
        Assert.That(entries[1].Change.Row, Is.Null, "A delete's ring entry must carry no row bytes, matching WAL staging's own convention.");
    }

    [Test]
    public async Task InsertUpdateDelete_OnAPersistentTable_EachRecordsAMatchingRingEntry() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);
        var expectedTableId = GetTableId(asm, "TestNs.RingDbAccountOps");

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Update(1, (dynamic)NewAccount(asm, 1, 500m)); return Result.Ok(); },
            PropagationMode.Confirmed);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Delete(1); return Result.Ok(); },
            PropagationMode.Confirmed);

        var entries = GetAllEntries(GetRing(db));

        Assert.That(entries, Has.Length.EqualTo(3));
        Assert.That(entries.Select(e => e.Change.TableId), Has.All.EqualTo(expectedTableId));
        Assert.That(entries.Select(e => e.Change.Kind), Is.EqualTo(new[] { ChangeKind.Insert, ChangeKind.Update, ChangeKind.Delete }));
        Assert.That(entries[0].Change.Row, Is.Not.Null);
        Assert.That(entries[1].Change.Row, Is.Not.Null);
        Assert.That(entries[2].Change.Row, Is.Null);
        Assert.That(entries.Select(e => e.Lsn), Is.EqualTo(new long[] { 0, 1, 2 }));
    }

    [Test]
    public async Task LsnSequence_IsSharedAcrossInstantAndPersistentTransactions() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Widget.Insert((dynamic)NewWidget(asm, 1, 100)); return Result.Ok(); },
            PropagationMode.Confirmed);
        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Account.Insert((dynamic)NewAccount(asm, 1, 100m)); return Result.Ok(); },
            PropagationMode.Confirmed);

        var entries = GetAllEntries(GetRing(db));

        Assert.That(entries, Has.Length.EqualTo(2));
        Assert.That(entries[0].Lsn, Is.EqualTo(0), "An Instant-only transaction still draws from the shared sequence.");
        Assert.That(entries[1].Lsn, Is.EqualTo(1), "The very next transaction, Persistent this time, continues the SAME sequence rather than starting its own.");
    }

    [Test]
    public async Task Insert_OnATableWithRingBufferDisabled_RecordsNothing() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => { ((dynamic)tx).Muted.Insert((dynamic)NewMuted(asm, 1)); return Result.Ok(); },
            PropagationMode.Confirmed);

        var entries = GetAllEntries(GetRing(db));

        Assert.That(entries, Is.Empty, "[Table(RingBuffer = false)] must suppress every ring.Record call for that table.");
    }

    [Test]
    public async Task Insert_OnAMutedTableAlongsideARingedTable_OnlyTheRingedTablesChangeAppears() {
        using var cold = ColdStore.Open(dir).Unwrap();
        var (db, txType, asm) = NewDb(cold);

        await (Task<Result>)GeneratorTestHost.RunTransactional(
            db, txType, (ctx, tx) => {
                dynamic dtx = tx;
                dtx.Widget.Insert((dynamic)NewWidget(asm, 1, 100));
                dtx.Muted.Insert((dynamic)NewMuted(asm, 1));
                return Result.Ok();
            }, PropagationMode.Confirmed);

        var entries = GetAllEntries(GetRing(db));

        Assert.That(entries, Has.Length.EqualTo(1));
        Assert.That(entries[0].Change.TableId, Is.EqualTo(GetTableId(asm, "TestNs.RingDbWidgetOps")));
    }
}
