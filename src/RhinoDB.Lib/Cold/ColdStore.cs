using RhinoDB.Native;

namespace RhinoDB.Lib.Cold;

public sealed class ColdStore : IDisposable {
    private const uint SafeNoSync = 0x10000; // MDBX_SAFE_NOSYNC
    private const uint CreateDbi = 0x40000; // MDBX_CREATE
    private const uint ReadOnlyTxn = 0x20000; // MDBX_RDONLY / MDBX_TXN_RDONLY
    private const ushort DefaultUnixMode = 0b110_100_100; // 0644: rw-r--r--

    private readonly MdbxEnvironment env;
    private readonly Dictionary<string, object> tables = [];
    private Task<int> pendingSync = Task.FromResult(0);

    internal Transaction? ActiveWriteTxn { get; private set; }
    internal bool IsScopeActive { get; private set; }

    private ColdStore(MdbxEnvironment env) => this.env = env;

    static public Result<ColdStore> Open(string path) {
        var rcCreate = MdbxEnvironment.Create(out MdbxEnvironment? env);
        if (rcCreate != 0 || env is null) return Result<ColdStore>.Error(MdbxErrorMapper.Map(rcCreate));

        env.SetMaxDbs(1024);
        env.SetGeometry(-1, -1, -1, -1, -1, -1); // -1 everywhere = "keep current or use default" (mdbx.h)

        var rcOpen = env.Open(path, flags: SafeNoSync, mode: DefaultUnixMode);
        if (rcOpen == 0) return Result<ColdStore>.Ok(new ColdStore(env));
        
        env.Dispose();
        return Result<ColdStore>.Error(MdbxErrorMapper.Map(rcOpen));
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

        pendingSync = pendingSync.ContinueWith(_ => env.Sync(force: true, nonblock: false), TaskScheduler.Default);
        return pendingSync;
    }

    internal ColdTable<TKey, TRow> OpenTable<TKey, TRow>(string name)
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

    internal Result<TRow> Peek<TKey, TRow>(ColdTable<TKey, TRow> table, TKey key)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct {
        var rc = env.BeginTxn(ReadOnlyTxn, out Transaction? txn);
        if (rc != 0 || txn is null) return Result<TRow>.Error(MdbxErrorMapper.Map(rc));

        using Transaction _ = txn;
        return table.Get(txn, key);
    }

    public void Dispose() {
        pendingSync.Wait();
        env.Dispose();
    }
}
