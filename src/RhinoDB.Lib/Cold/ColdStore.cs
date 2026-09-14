using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;
using RhinoDB.Native;

namespace RhinoDB.Lib.Cold;

public sealed class ColdStore : IDisposable {
    private const uint CreateDbi = 0x40000;
    private const uint ReadOnlyTxn = 0x20000;
    private const ushort DefaultUnixMode = 0b110_100_100;
    private const uint SafeNoSync = 0x10000;
    private const string WalFileName = "wal.dat";

    private readonly MdbxEnvironment env;
    private readonly WriteAheadLog wal;
    private readonly CheckpointEngine checkpoint;
    private readonly Dictionary<string, object> tables = [];
    private readonly Dictionary<uint, uint> tableDbisById = [];
    private readonly List<WalChange> currentOperationChanges = [];
    private DecodedWalEntry[] pendingRecoveryEntries;
    private long nextLsn;

    public bool IsScopeActive { get; private set; }

    private ColdStore(MdbxEnvironment env, WriteAheadLog wal, DecodedWalEntry[] pendingRecoveryEntries, long nextLsn) {
        this.env = env;
        this.wal = wal;
        this.pendingRecoveryEntries = pendingRecoveryEntries;
        this.nextLsn = nextLsn;
        checkpoint = new CheckpointEngine(env, wal);
    }

    static public Result<ColdStore> Open(string path, nint sizeUpperBytes = -1, nint sizeNowBytes = -1) {
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

        var walPath = Path.Combine(path, WalFileName);
        WriteAheadLog wal;
        var tailEntries = Array.Empty<DecodedWalEntry>();

        if (File.Exists(walPath)) {
            var openResult = WriteAheadLog.Open(walPath);
            if (openResult.IsError()) { env.Dispose(); return Result<ColdStore>.Error(openResult.GetError()); }
            (wal, tailEntries) = openResult.Unwrap();
        } else {
            var createResult = WriteAheadLog.Create(walPath, Guid.NewGuid());
            if (createResult.IsError()) { env.Dispose(); return Result<ColdStore>.Error(createResult.GetError()); }
            wal = createResult.Unwrap();
        }

        var checkpointEngine = new CheckpointEngine(env, wal);
        var checkpointedLsnResult = checkpointEngine.ReadCheckpointedLsn();
        if (checkpointedLsnResult.IsError()) {
            wal.Dispose();
            env.Dispose();
            return Result<ColdStore>.Error(checkpointedLsnResult.GetError());
        }
        var checkpointedLsn = checkpointedLsnResult.Unwrap();

        var operationEntries = tailEntries.Where(e => e.Kind == WalEntryKind.Operation).ToArray();
        var maxTailLsn = operationEntries.Length > 0 ? operationEntries.Max(e => e.Lsn) : checkpointedLsn;
        var pendingRecovery = operationEntries.Where(e => e.Lsn > checkpointedLsn).ToArray();

        return Result<ColdStore>.Ok(new ColdStore(env, wal, pendingRecovery, Math.Max(checkpointedLsn, maxTailLsn)));
    }

    public Result CompleteRecovery() {
        var entries = pendingRecoveryEntries;
        pendingRecoveryEntries = [];
        if (entries.Length == 0) return Result.Ok();

        var latest = new Dictionary<(uint TableId, string KeyBase64), WalChange>();
        foreach (var entry in entries)
            foreach (var change in entry.Changes)
                latest[(change.TableId, Convert.ToBase64String(change.Key))] = change;

        var residentRows = latest.Values.Where(c => c.Kind != ChangeKind.Delete)
            .Select(c => new CheckpointRow(c.TableId, c.Key, c.Row!)).ToArray();
        var deletedKeys = latest.Values.Where(c => c.Kind == ChangeKind.Delete)
            .Select(c => (c.TableId, c.Key)).ToArray();

        return checkpoint.RunCheckpoint(nextLsn, tableDbisById, residentRows, deletedKeys).GetAwaiter().GetResult();
    }

    internal void BeginScope() { IsScopeActive = true; currentOperationChanges.Clear(); }

    public void Stage(uint tableId, ChangeKind kind, byte[] key, byte[]? row) =>
        currentOperationChanges.Add(new WalChange(tableId, kind, key, row));

    internal Task<DbError?> EndScope(bool commit, PropagationMode mode) {
        IsScopeActive = false;
        if (!commit || currentOperationChanges.Count == 0) { currentOperationChanges.Clear(); return Task.FromResult<DbError?>(null); }

        var lsn = ++nextLsn;
        var changes = currentOperationChanges.ToArray();
        currentOperationChanges.Clear();

        if (mode == PropagationMode.Confirmed) return wal.AppendConfirmed(lsn, WalEntryKind.Operation, changes);

        wal.AppendOptimistic(lsn, WalEntryKind.Operation, changes);
        return Task.FromResult<DbError?>(null);
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
        tableDbisById[TableIdHash.Compute(name)] = dbi;
        return table;
    }

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
        wal.Dispose();
        env.Dispose();
    }
}
