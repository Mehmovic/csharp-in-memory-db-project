using RhinoDB.Native;

namespace RhinoDB.Lib.Cold;

public sealed class ColdStore : IDisposable {
    private const uint SafeNoSync = 0x10000;
    private const uint CreateDbi = 0x40000;
    private const uint ReadOnlyTxn = 0x20000;
    private const ushort DefaultUnixMode = 0b110_100_100;

    private const int MdbxResultTrue = -1;

    private readonly MdbxEnvironment env;
    private readonly Dictionary<string, object> tables = [];
    private readonly TimeSpan commitCoalescingWindow;
    private readonly Lock syncLock = new Lock();
    private Task<int> pendingSync = Task.FromResult(0);
    private TaskCompletionSource<int>? openBatch;

    internal Transaction? ActiveWriteTxn { get; private set; }

    public bool IsScopeActive { get; private set; }

    private ColdStore(MdbxEnvironment env, TimeSpan commitCoalescingWindow) {
        this.env = env;
        this.commitCoalescingWindow = commitCoalescingWindow;
    }

    static public Result<ColdStore> Open(string path, nint sizeUpperBytes = -1, nint sizeNowBytes = -1, TimeSpan commitCoalescingWindow = default) {
        var isFreshDirectory = !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();

        var rcCreate = MdbxEnvironment.Create(out MdbxEnvironment? env);
        if (rcCreate != 0 || env is null) return Result<ColdStore>.Error(MdbxErrorMapper.Map(rcCreate));

        env.SetMaxDbs(1024);
        env.SetGeometry(-1, sizeNowBytes, sizeUpperBytes, -1, -1, -1);

        var rcOpen = env.Open(path, flags: SafeNoSync, mode: DefaultUnixMode);
        if (rcOpen != 0) {
            env.Dispose();
            return Result<ColdStore>.Error(MdbxErrorMapper.Map(rcOpen));
        }

        if (isFreshDirectory && !DirectorySync.TrySync(path, out _)) {
            env.Dispose();
            return Result<ColdStore>.Error(DbError.ColdStorageDirectorySyncFailed());
        }

        return Result<ColdStore>.Ok(new ColdStore(env, commitCoalescingWindow));
    }

    internal void BeginScope() => IsScopeActive = true;

    internal Transaction EnsureWriteTxn() {
        if (ActiveWriteTxn is { } active) return active;

        var rc = env.BeginTxn(0, out Transaction? txn);
        if (rc != 0 || txn is null) throw MdbxErrorMapper.Map(rc).ToException();

        ActiveWriteTxn = txn;
        return txn;
    }

    internal Task<int> EndScope(bool commit, bool forceSync) {
        IsScopeActive = false;

        Transaction? txn = ActiveWriteTxn;
        ActiveWriteTxn = null;
        if (txn is null) return Task.FromResult(0);

        var rc = commit ? txn.Commit() : txn.Abort();
        if (!commit || !forceSync || rc != 0) return Task.FromResult(rc);

        return commitCoalescingWindow > TimeSpan.Zero ? ScheduleCoalescedSync() : FireSyncImmediately();
    }

    private Task<int> FireSyncImmediately() {
        pendingSync = pendingSync.ContinueWith(_ => RunSync(), TaskScheduler.Default);
        return pendingSync;
    }

    private Task<int> ScheduleCoalescedSync() {
        lock (syncLock) {
            if (openBatch is { } batch) return batch.Task;

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            openBatch = tcs;
            var previous = pendingSync;
            pendingSync = RunCoalescedBatch(tcs, previous);
            return tcs.Task;
        }
    }

    private async Task<int> RunCoalescedBatch(TaskCompletionSource<int> tcs, Task<int> previous) {
        await Task.Delay(commitCoalescingWindow);
        lock (syncLock) { if (ReferenceEquals(openBatch, tcs)) openBatch = null; }

        // Native sync calls must stay strictly serialized (never two mdbx_env_sync_ex
        // calls in flight at once) - awaiting whatever batch preceded this one preserves
        // that guarantee exactly like the un-coalesced path's ContinueWith chain did.
        await previous;
        var result = RunSync();
        tcs.SetResult(result);
        return result;
    }

    private int RunSync() {
        var syncRc = env.Sync(force: true, nonblock: false);
        return syncRc == MdbxResultTrue ? 0 : syncRc;
    }

    public ColdTable<TKey, TRow> OpenTable<TKey, TRow>(string name)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct {
        if (tables.TryGetValue(name, out var existing)) return (ColdTable<TKey, TRow>)existing;

        var rcTxn = env.BeginTxn(0, out Transaction? txn);
        if (rcTxn != 0 || txn is null) throw MdbxErrorMapper.Map(rcTxn).ToException();

        var rcDbi = txn.OpenDbi(name, CreateDbi, out var dbi);
        if (rcDbi != 0) {
            txn.Abort();
            throw MdbxErrorMapper.Map(rcDbi).ToException();
        }

        var rcCommit = txn.Commit();
        if (rcCommit != 0) throw MdbxErrorMapper.Map(rcCommit).ToException();

        var table = new ColdTable<TKey, TRow>(dbi);
        tables[name] = table;
        return table;
    }

    public Result Put<TKey, TRow>(ColdTable<TKey, TRow> table, TKey key, TRow row)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct
        => table.Put(EnsureWriteTxn(), key, row);

    public Result<TRow> Get<TKey, TRow>(ColdTable<TKey, TRow> table, TKey key)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct
        => table.Get(EnsureWriteTxn(), key);

    public Result Delete<TKey, TRow>(ColdTable<TKey, TRow> table, TKey key)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct
        => table.Delete(EnsureWriteTxn(), key);

    public Result<TRow> Peek<TKey, TRow>(ColdTable<TKey, TRow> table, TKey key)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct {
        var rc = env.BeginTxn(ReadOnlyTxn, out Transaction? txn);
        if (rc != 0 || txn is null) return Result<TRow>.Error(MdbxErrorMapper.Map(rc));

        using Transaction _ = txn;
        return table.Get(txn, key);
    }

    public IEnumerable<(TKey Key, TRow Row)> ScanAll<TKey, TRow>(ColdTable<TKey, TRow> table)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct {
        var rc = env.BeginTxn(ReadOnlyTxn, out Transaction? txn);
        if (rc != 0 || txn is null) throw MdbxErrorMapper.Map(rc).ToException();

        using Transaction _ = txn;
        foreach ((TKey Key, TRow Row) pair in table.ScanAll(txn)) yield return pair;
    }

    public void Dispose() {
        pendingSync.Wait();
        env.Dispose();
    }
}
