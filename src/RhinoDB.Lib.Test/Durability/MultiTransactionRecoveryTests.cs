using MemoryPack;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Cold.Test;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Durability.Test;

// Crash recovery for multi-database transaction chains, exercised against real WAL files, real libmdbx and a
// real chains.log - a "crash" here is just a participant whose ChainPrepare reached disk and then nothing
// else did (no ChainCommit/ChainAbort marker, no ChainLog decision). The policy under test: complete the
// commit whenever every participant's prepare is on disk, and abort only when completing is impossible.
public class MultiTransactionRecoveryTests {
    private const string Table = "accounts";
    private const string RootId = ChainLog.RootParticipantId;
    private const string ChildId = "Children/SessionDb/s1";

    private string rootDir = "";
    private string childDir = "";

    [SetUp]
    public void SetUp() {
        rootDir = Path.Combine(Path.GetTempPath(), "rhinodb-chain-recovery-tests", Guid.NewGuid().ToString("N"));
        childDir = Path.Combine(rootDir, "Children", "SessionDb", "s1");
        Directory.CreateDirectory(childDir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(rootDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    static private readonly string[] BothParticipants = [RootId, ChildId];

    static private async Task Prepare(string dir, Guid chainId, int key, bool? marker = null) {
        using var store = ColdStore.Open(dir).Unwrap();
        store.OpenTable<int, Account>(Table);
        store.BeginScope();
        store.Stage(NameHash.Compute(Table), ChangeKind.Insert, MemoryPackSerializer.Serialize(key),
            MemoryPackSerializer.Serialize(new Account(key, $"owner-{key}", 10m)));

        var error = await store.AppendChainPrepare(lsn: (ulong)key, chainId, BothParticipants, dependsOn: []);
        Assert.That(error, Is.Null);
        store.EndChainScope();
        // No marker = the process died right after this prepare became durable.
        if (marker is { } commit) await store.AppendChainMarker(chainId, commit);
    }

    static private async Task<(Result Recovery, bool RowPresent)> Recover(string dir, ChainLog? log, string participantId, int key) {
        using var store = ColdStore.Open(dir).Unwrap();
        var accounts = store.OpenTable<int, Account>(Table);
        var recovery = await store.CompleteRecoveryAsync(log?.ResolverFor(participantId));
        return (recovery, store.Peek(accounts, key).IsOk());
    }

    [Test]
    public async Task EveryParticipantPrepared_NothingElseReachedDisk_RecoveryCompletesTheCommitEverywhere() {
        var chainId = Guid.NewGuid();
        await Prepare(rootDir, chainId, key: 1);
        await Prepare(childDir, chainId, key: 2);
        var log = ChainLog.Open(rootDir).Unwrap();

        var root = await Recover(rootDir, log, RootId, key: 1);
        var child = await Recover(childDir, log, ChildId, key: 2);

        Assert.That(root.Recovery.IsOk() && child.Recovery.IsOk(), Is.True);
        Assert.That(root.RowPresent, Is.True, "every prepare was durable - the commit is completable, so it must be completed, not reverted.");
        Assert.That(child.RowPresent, Is.True);
        Assert.That(log.TryGetDecision(chainId, out var commit) && commit, Is.True, "the roll-forward is recorded centrally before anything is truncated.");
    }

    [Test]
    public async Task OnlyOneParticipantPrepared_RecoveryAbortsBecauseTheOtherShareDiedWithTheProcess() {
        var chainId = Guid.NewGuid();
        await Prepare(rootDir, chainId, key: 1);
        var log = ChainLog.Open(rootDir).Unwrap();

        var root = await Recover(rootDir, log, RootId, key: 1);

        Assert.That(root.Recovery.IsOk(), Is.True);
        Assert.That(root.RowPresent, Is.False, "the child's share existed only in memory - completing is impossible, so the root's share is dropped.");
        Assert.That(log.TryGetDecision(chainId, out var commit) && !commit, Is.True);
    }

    [Test]
    public async Task RootRecoversAndTruncatesFirst_ChildRecoveringLaterStillCompletesTheSameCommit() {
        // The invariant this guards: once the root checkpoints, its prepare is gone from its WAL. Had the
        // decision not been recorded first, the child would find no prepare at the root and wrongly abort.
        var chainId = Guid.NewGuid();
        await Prepare(rootDir, chainId, key: 1);
        await Prepare(childDir, chainId, key: 2);
        var log = ChainLog.Open(rootDir).Unwrap();

        var root = await Recover(rootDir, log, RootId, key: 1);
        Assert.That(root.RowPresent, Is.True);
        var rootTail = WriteAheadLog.ReadEntriesShared(Path.Combine(rootDir, "wal.dat")).Unwrap();
        Assert.That(rootTail.Any(e => e.ChainId == chainId), Is.False, "the root's prepare was checkpointed and truncated.");

        var reopenedLog = ChainLog.Open(rootDir).Unwrap();
        var child = await Recover(childDir, reopenedLog, ChildId, key: 2);

        Assert.That(child.RowPresent, Is.True);
    }

    [Test]
    public async Task ExplicitAbortInTheChainLog_WinsEvenWhenEveryPrepareIsOnDisk() {
        // What a live Commit records when one participant's prepare fsync failed after others succeeded.
        var chainId = Guid.NewGuid();
        await Prepare(rootDir, chainId, key: 1);
        await Prepare(childDir, chainId, key: 2);
        var log = ChainLog.Open(rootDir).Unwrap();
        Assert.That(log.Record(chainId, commit: false, BothParticipants).IsOk(), Is.True);

        var root = await Recover(rootDir, log, RootId, key: 1);
        var child = await Recover(childDir, log, ChildId, key: 2);

        Assert.That(root.RowPresent || child.RowPresent, Is.False);
    }

    [Test]
    public async Task ACommitMarkerInTheParticipantsOwnWal_IsHonouredWithoutScanningAndRecordedCentrally() {
        var chainId = Guid.NewGuid();
        await Prepare(rootDir, chainId, key: 1, marker: true);
        // The child never got its prepare durable - a scan would say "abort", but the root's marker proves the
        // live chain had already passed its commit point, so it can't be reverted now.
        var log = ChainLog.Open(rootDir).Unwrap();

        var root = await Recover(rootDir, log, RootId, key: 1);

        Assert.That(root.RowPresent, Is.True);
        Assert.That(log.TryGetDecision(chainId, out var commit) && commit, Is.True);
    }

    [Test]
    public async Task AnAbortMarkerInTheParticipantsOwnWal_DropsItsShare() {
        var chainId = Guid.NewGuid();
        await Prepare(rootDir, chainId, key: 1, marker: false);
        await Prepare(childDir, chainId, key: 2);
        var log = ChainLog.Open(rootDir).Unwrap();

        var child = await Recover(childDir, log, ChildId, key: 2);

        Assert.That(child.RowPresent, Is.False, "the root's own WAL says this chain aborted - that's authoritative over 'every prepare is on disk'.");
    }

    [Test]
    public async Task AnUndecidedPrepareWithNoResolver_RefusesToRecoverAndKeepsTheEvidence() {
        var chainId = Guid.NewGuid();
        await Prepare(rootDir, chainId, key: 1);

        var refused = await Recover(rootDir, log: null, RootId, key: 1);

        Assert.That(refused.Recovery.IsError(), Is.True);
        Assert.That(refused.Recovery.GetError().Kind, Is.EqualTo(ErrorKind.ChainResolutionUnavailable));
        using var reopened = ColdStore.Open(rootDir).Unwrap();
        Assert.That(reopened.PendingChainPrepares, Has.Length.EqualTo(1), "refusing must not truncate - the prepare is still there to decide later.");
    }

    [Test]
    public async Task RecoveredLsn_CountsChainPrepares_SoTheNextOperationNeverReusesTheirLsn() {
        await Prepare(rootDir, Guid.NewGuid(), key: 7);

        using var reopened = ColdStore.Open(rootDir).Unwrap();

        Assert.That(reopened.RecoveredLsn, Is.EqualTo(7UL));
    }

    // ---- Genesis replay's view of the live tail ----

    static private DecodedWalEntry[] ReplayTail(string dir, ChainLog? log, string participantId, out Result<DecodedWalEntry[]> result) {
        using var store = ColdStore.Open(dir).Unwrap();
        if (log is not null) store.AttachChainResolver(log.ResolverFor(participantId));
        result = store.ResolveReplayTail();
        return result.IsOk() ? result.Unwrap() : [];
    }

    [Test]
    public async Task ReplayTail_ACommittedPrepareStillInTheLiveWal_IsReplayedFromItsOwnMarkerWithNoResolver() {
        // Replay never truncates, so this store's own commit marker settles it - no chain log needed.
        await Prepare(rootDir, Guid.NewGuid(), key: 1, marker: true);

        var tail = ReplayTail(rootDir, log: null, RootId, out var result);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(tail, Has.Length.EqualTo(1));
        Assert.That(tail[0].Kind, Is.EqualTo(WalEntryKind.Operation), "replayed exactly like an ordinary operation.");
        Assert.That(tail[0].Lsn, Is.EqualTo(1UL));
    }

    [Test]
    public async Task ReplayTail_AnAbortedPrepare_IsLeftOut() {
        await Prepare(rootDir, Guid.NewGuid(), key: 1, marker: false);

        var tail = ReplayTail(rootDir, log: null, RootId, out var result);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(tail, Is.Empty);
    }

    [Test]
    public async Task ReplayTail_AMarkerlessPrepare_IsDecidedThroughTheResolver_AndCompletedWhenEveryoneIsPrepared() {
        var chainId = Guid.NewGuid();
        await Prepare(rootDir, chainId, key: 1);
        await Prepare(childDir, chainId, key: 2);
        var log = ChainLog.Open(rootDir).Unwrap();

        var tail = ReplayTail(rootDir, log, RootId, out var result);

        Assert.That(result.IsOk(), Is.True);
        Assert.That(tail.Select(e => e.Lsn), Is.EqualTo(new[] { 1UL }));
        Assert.That(log.TryGetDecision(chainId, out var commit) && commit, Is.True,
            "replay records what it decided, so the next normal recovery can't decide differently.");
    }

    [Test]
    public async Task ReplayTail_AMarkerlessPrepareWithNoResolver_RefusesRatherThanSkippingIt() {
        await Prepare(rootDir, Guid.NewGuid(), key: 1);

        ReplayTail(rootDir, log: null, RootId, out var result);

        Assert.That(result.IsError(), Is.True);
        Assert.That(result.GetError().Kind, Is.EqualTo(ErrorKind.ChainResolutionUnavailable));
    }

    [Test]
    public async Task ReplayTail_KeepsOrdinaryOperationsAndCommittedPreparesInLsnOrder() {
        using (var store = ColdStore.Open(rootDir).Unwrap()) {
            store.OpenTable<int, Account>(Table);
            store.BeginScope();
            store.Stage(NameHash.Compute(Table), ChangeKind.Insert, MemoryPackSerializer.Serialize(5), MemoryPackSerializer.Serialize(new Account(5, "o", 1m)));
            Assert.That(await store.EndScope(commit: true, PropagationMode.Confirmed, lsn: 5), Is.Null);
        }
        await Prepare(rootDir, Guid.NewGuid(), key: 3, marker: true);

        var tail = ReplayTail(rootDir, log: null, RootId, out _);

        Assert.That(tail.Select(e => e.Lsn), Is.EqualTo(new[] { 3UL, 5UL }));
    }

    // ---- ChainLog itself ----

    [Test]
    public async Task ChainLog_DropsAChainOnReopenOnceEveryParticipantAcknowledgedConsumingIt() {
        var chainId = Guid.NewGuid();
        await Prepare(rootDir, chainId, key: 1);
        await Prepare(childDir, chainId, key: 2);
        var log = ChainLog.Open(rootDir).Unwrap();
        await Recover(rootDir, log, RootId, key: 1);
        Assert.That(ChainLog.Open(rootDir).Unwrap().RetainedChainCount, Is.EqualTo(1), "the child hasn't consumed its prepare yet - the decision must survive.");

        await Recover(childDir, log, ChildId, key: 2);

        Assert.That(ChainLog.Open(rootDir).Unwrap().RetainedChainCount, Is.EqualTo(0));
    }

    [Test]
    public void ChainLog_FirstRecordedDecisionWins() {
        var log = ChainLog.Open(rootDir).Unwrap();
        var chainId = Guid.NewGuid();

        log.Record(chainId, commit: true, BothParticipants);
        var second = log.Record(chainId, commit: false, BothParticipants);

        Assert.That(second.Unwrap(), Is.True);
        Assert.That(ChainLog.Open(rootDir).Unwrap().TryGetDecision(chainId, out var commit) && commit, Is.True);
    }

    [Test]
    public void ChainLog_ATornTailIsIgnoredAndEverythingBeforeItSurvives() {
        var chainId = Guid.NewGuid();
        ChainLog.Open(rootDir).Unwrap().Record(chainId, commit: true, BothParticipants);
        using (var file = new FileStream(Path.Combine(rootDir, ChainLog.FileName), FileMode.Append))
            file.Write([0x40, 0, 0, 0, 1, 2, 3]);

        var reopened = ChainLog.Open(rootDir).Unwrap();

        Assert.That(reopened.TryGetDecision(chainId, out var commit) && commit, Is.True);
        Assert.That(reopened.Record(Guid.NewGuid(), commit: false, BothParticipants).IsOk(), Is.True, "appending after a repaired tail still works.");
    }
}
