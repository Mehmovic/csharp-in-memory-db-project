using System.Collections.Concurrent;

using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Lib.Hosting;

public sealed class ChildDatabaseOptions<TChildDb, TTx, TKey>
    where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
    public Func<ColdStore, TChildDb>? CreateDb { get; set; }
    public Func<TKey, string>? KeyToDirectoryName { get; set; }
}

internal interface IChildDatabaseRegistry {
    void AttachRootColdPath(string rootColdPath);
    void AttachChainLog(ChainLog chainLog);
    string ParticipantIdFor(object key);
    void CloseAllBestEffort();
}

internal sealed class ChildDatabaseRegistry<TChildDb, TTx, TKey>(ChildDatabaseOptions<TChildDb, TTx, TKey> options)
    : IChildDatabaseRegistry
    where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
    private const string ChildrenDirectoryName = "Children";

    private readonly ConcurrentDictionary<TKey, TChildDb> active = new ConcurrentDictionary<TKey, TChildDb>();
    // Serializes opening a child's directory against deleting one: without it, disposing an inactive child could
    // delete its directory while a concurrent activation is opening it, and two activations of one key could
    // both open it. Draining an active child stays outside, so a slow drain never blocks other activations.
    private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
    private string? baseDirectory;
    private ChainLog? chainLog;

    public void AttachRootColdPath(string rootColdPath) =>
        baseDirectory = Path.Combine(rootColdPath, ChildrenDirectoryName, typeof(TChildDb).Name);

    public void AttachChainLog(ChainLog log) => chainLog = log;

    public string ParticipantIdFor(object key) => $"{ChildrenDirectoryName}/{typeof(TChildDb).Name}/{DirectoryNameFor((TKey)key)}";

    public async Task<Result<TChildDb>> GetOrActivateAsync(TKey key, CancellationToken ct) {
        if (active.TryGetValue(key, out var existing)) return existing;
        if (options.CreateDb is not { } createDb)
            return Result<TChildDb>.Error(DbError.SystemFailure(new InvalidOperationException(
                $"AddChildDatabase<{typeof(TChildDb).Name}>: CreateDb was not configured.")));

        await lifecycleGate.WaitAsync(ct);
        try {
            if (active.TryGetValue(key, out existing)) return existing;
            return await ActivateUnderGateAsync(key, createDb);
        } finally {
            lifecycleGate.Release();
        }
    }

    private async Task<Result<TChildDb>> ActivateUnderGateAsync(TKey key, Func<ColdStore, TChildDb> createDb) {
        var dir = Path.Combine(baseDirectory!, DirectoryNameFor(key));
        Directory.CreateDirectory(dir);
        var coldResult = ColdStore.Open(dir);
        if (coldResult.IsError()) return coldResult.Void();
        var cold = coldResult.Unwrap();
        if (chainLog is not null) cold.AttachChainResolver(chainLog.ResolverFor(ParticipantIdFor(key)));

        var db = createDb(cold);
        if (cold.WasFreshlyCreated) {
            var initResult = await db.OnInitAsync();
            if (initResult.IsError()) { cold.Dispose(); return initResult; }
        }

        var recoveryResult = await cold.CompleteRecoveryAsync();
        if (recoveryResult.IsError()) { cold.Dispose(); return recoveryResult; }

        var startResult = await db.OnStartAsync();
        if (startResult.IsError()) { cold.Dispose(); return startResult; }

        active[key] = db;
        return db;
    }

    // Works whether or not the child is activated: after a restart a child's directory exists on disk long before
    // anything touches it again, and disposing it must still delete it. An inactive child is never activated just
    // to be deleted - no recovery, OnInit or OnStart for data that's about to go.
    public async Task<Result> DisposeAsync(TKey key, CancellationToken drainCt) {
        while (true) {
            TChildDb? drained = null;
            if (active.TryGetValue(key, out var db)) {
                db.BeginDraining();
                try {
                    await db.DrainAsync().WaitAsync(drainCt);
                } catch (OperationCanceledException) {
                    return Result.Error(DbError.SystemFailure(new TimeoutException(
                        $"Draining child database '{typeof(TChildDb).Name}' key '{key}' did not finish before the "
                        + "caller-supplied CancellationToken fired - the child was NOT disposed and its data was NOT "
                        + "deleted; it is still draining and will reject new work - retry DisposeChildAsync to finish.")));
                }
                drained = db;
            }

            await lifecycleGate.WaitAsync(CancellationToken.None);
            try {
                // Activated by someone else after we looked - it hasn't been drained, so drain it first.
                if (active.TryGetValue(key, out var current) && !ReferenceEquals(current, drained)) continue;

                if (active.TryRemove(key, out var removed)) removed.Cold?.Dispose();

                var dir = Path.Combine(baseDirectory!, DirectoryNameFor(key));
                if (!Directory.Exists(dir)) return Result.Ok();

                var settled = SettleChainsBeforeDeleting(dir, ParticipantIdFor(key));
                if (settled.IsError()) return settled;

                try { Directory.Delete(dir, recursive: true); }
                catch (DirectoryNotFoundException) { /* already gone */ }
                return Result.Ok();
            } finally {
                lifecycleGate.Release();
            }
        }
    }

    private Result SettleChainsBeforeDeleting(string dir, string participantId) {
        if (chainLog is null) return Result.Ok();

        var entriesResult = WriteAheadLog.ReadEntriesShared(Path.Combine(dir, ColdStore.WalFileName));
        if (entriesResult.IsError()) return entriesResult.Void();
        var entries = entriesResult.Unwrap();

        var markers = new Dictionary<Guid, bool>();
        foreach (var entry in entries) {
            if (entry.Kind == WalEntryKind.ChainCommit) markers[entry.ChainId] = true;
            if (entry.Kind == WalEntryKind.ChainAbort) markers[entry.ChainId] = false;
        }

        var resolver = chainLog.ResolverFor(participantId);
        var settled = new List<Guid>();
        foreach (var prepare in entries.Where(e => e.Kind == WalEntryKind.ChainPrepare)) {
            bool? known = markers.TryGetValue(prepare.ChainId, out var marker) ? marker : null;
            var decision = resolver.Resolve(prepare.ChainId, prepare.ChainParticipants, known);
            if (decision.IsError()) return decision.Void();
            settled.Add(prepare.ChainId);
        }
        resolver.Acknowledge(settled);
        return Result.Ok();
    }

    public void CloseAllBestEffort() {
        foreach (var db in active.Values) {
            try { db.Cold?.Dispose(); } catch { /* best-effort - shutdown must not throw */ }
        }
    }

    private string DirectoryNameFor(TKey key) => (options.KeyToDirectoryName ?? DefaultKeyToDirectoryName)(key);

    static private string DefaultKeyToDirectoryName(TKey key) =>
        key.ToString() ?? throw new InvalidOperationException(
            $"Child database key of type '{typeof(TKey).Name}' produced a null directory name from ToString() "
            + "- supply ChildDatabaseOptions.KeyToDirectoryName explicitly.");
}
