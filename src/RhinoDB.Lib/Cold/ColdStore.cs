using MemoryPack;

using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;
using RhinoDB.Native;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Cold;

public sealed class ColdStore : IDisposable {
    private const uint CreateDbi = 0x40000;
    private const uint ReadOnlyTxn = 0x20000;
    private const ushort DefaultUnixMode = 0b110_100_100;
    private const uint SafeNoSync = 0x10000;
    private const string WalFileName = "wal.dat";

    private readonly MdbxEnvironment env;
    private readonly CheckpointEngine checkpoint;
    private readonly Dictionary<string, object> tables = [];
    private readonly Dictionary<uint, uint> tableDbisById = [];
    private readonly List<WalChange> currentOperationChanges = [];
    private readonly EvictionBatch evictionBatch;
    private readonly Dictionary<uint, EvictionDropRegistration> evictionDrops = [];
    private readonly List<EvictionCandidate> drainedEvictions = [];
    private readonly List<EvictionCandidate> appliedEvictions = [];

    internal readonly WriteAheadLog Wal;

    public bool IsScopeActive { get; private set; }

    public string DirectoryPath { get; }

    public int WalGeneration => (int)Wal.Generation;

    public DecodedWalEntry[] PendingWalTail { get; private set; }

    public ulong RecoveredLsn { get; }

    public bool WasFreshlyCreated { get; }

    private ColdStore(
        MdbxEnvironment env,
        WriteAheadLog wal,
        string directoryPath,
        string archiveDirectory,
        DecodedWalEntry[] pendingRecoveryEntries,
        ulong recoveredLsn,
        long evictionBatchThresholdBytes,
        bool wasFreshlyCreated
    ) {
        this.env = env;
        Wal = wal;
        DirectoryPath = directoryPath;
        PendingWalTail = pendingRecoveryEntries;
        RecoveredLsn = recoveredLsn;
        WasFreshlyCreated = wasFreshlyCreated;
        checkpoint = new CheckpointEngine(env, wal, archiveDirectory);
        evictionBatch = new EvictionBatch(evictionBatchThresholdBytes);
    }

    static public Result<ColdStore> Open(
        string path,
        nint sizeUpperBytes = -1,
        nint sizeNowBytes = -1,
        long evictionBatchThresholdBytes = EvictionBatch.DefaultSizeThresholdBytes
    ) {
        var isFreshDirectory = !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();

        var rcCreate = MdbxEnvironment.Create(out var env);
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
        var archiveDirectory = Path.Combine(path, WalArchive.ArchiveDirectoryName);
        WriteAheadLog wal;
        var tailEntries = Array.Empty<DecodedWalEntry>();

        if (File.Exists(walPath)) {
            var openResult = WriteAheadLog.Open(walPath);
            if (openResult.IsError()) {
                env.Dispose();
                return Result<ColdStore>.Error(openResult.GetError());
            }
            (wal, tailEntries) = openResult.Unwrap();
        } else {
            var createResult = WriteAheadLog.Create(walPath, Guid.NewGuid(), 0);
            if (createResult.IsError()) {
                env.Dispose();
                return Result<ColdStore>.Error(createResult.GetError());
            }
            wal = createResult.Unwrap();
        }

        var checkpointEngine = new CheckpointEngine(env, wal, archiveDirectory);
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

        return Result<ColdStore>.Ok(
            new ColdStore(
                env,
                wal,
                path,
                archiveDirectory,
                pendingRecovery,
                Math.Max(checkpointedLsn, maxTailLsn),
                evictionBatchThresholdBytes,
                isFreshDirectory
            )
        );
    }

    public Result CompleteRecovery() =>
        CompleteRecoveryAsync().GetAwaiter().GetResult();

    public Task<Result> CompleteRecoveryAsync() {
        var entries = PendingWalTail;
        PendingWalTail = [];
        if (entries.Length == 0) return Task.FromResult(Result.Ok());

        var latest = new Dictionary<(uint TableId, byte[] Key), WalChange>(WalKeyComparer.Instance);
        foreach (var entry in entries)
        foreach (var change in entry.Changes)
            latest[(change.TableId, change.Key)] = change;

        var residentRows = latest.Values.Where(c => c.Kind != ChangeKind.Delete)
            .Select(c => new CheckpointRow(c.TableId, c.Key, c.Row!)).ToArray();
        var deletedKeys = latest.Values.Where(c => c.Kind == ChangeKind.Delete)
            .Select(c => (c.TableId, c.Key)).ToArray();

        return checkpoint.RunCheckpoint(RecoveredLsn, tableDbisById, residentRows, deletedKeys, entries);
    }

    internal void BeginScope() {
        ApplyPendingEvictions();
        IsScopeActive = true;
        currentOperationChanges.Clear();
    }

    private void ApplyPendingEvictions() {
        evictionBatch.DrainStaged(into: drainedEvictions);
        if (drainedEvictions.Count == 0) return;

        var appliedResult = EvictionBatchApplier.Apply(
            env,
            tableDbisById,
            drainedEvictions,
            (tableId, key) => evictionDrops.TryGetValue(tableId, out var registration)
                ? registration.TryGetCurrentRow(key)
                : null,
            appliedInto: appliedEvictions
        );
        if (appliedResult.IsError()) return;

        var applied = appliedResult.Unwrap();
        for (var i = 0; i < applied.Count; i++) {
            var candidate = applied[i];
            if (evictionDrops.TryGetValue(candidate.TableId, out var registration))
                registration.Drop(candidate.Key);
        }
    }

    public void StageEviction(uint tableId, byte[] key) {
        if (evictionBatch.Stage(tableId, key)) ApplyPendingEvictions();
    }

    public void RegisterEvictionDrop(uint tableId, Func<byte[], byte[]?> tryGetCurrentRow, Action<byte[]> drop) =>
        evictionDrops[tableId] = new EvictionDropRegistration(tryGetCurrentRow, drop);

    // Generated-code seam only: called by emitted `Apply` bodies to record each
    // change into the operation's staging buffer. Never call from user code.
    public void Stage(uint tableId, ChangeKind kind, byte[] key, byte[]? row) =>
        currentOperationChanges.Add(new WalChange(tableId, kind, key, row));

    internal Task<DbError?> EndScope(bool commit, PropagationMode mode, ulong lsn) {
        IsScopeActive = false;
        if (!commit || currentOperationChanges.Count == 0) {
            currentOperationChanges.Clear();
            return Task.FromResult<DbError?>(null);
        }

        Task<DbError?> result;
        if (mode == PropagationMode.Confirmed) {
            result = Wal.AppendConfirmed(lsn, WalEntryKind.Operation, currentOperationChanges);
        } else {
            Wal.AppendOptimistic(lsn, WalEntryKind.Operation, currentOperationChanges);
            result = Task.FromResult<DbError?>(null);
        }

        currentOperationChanges.Clear();
        return result;
    }

    public ColdTable<TKey, TRow> OpenTable<TKey, TRow>(string name)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct =>
        OpenTable<TKey, TRow>(
            name,
            k => MemoryPackSerializer.Serialize(k),
            b => MemoryPackSerializer.Deserialize<TKey>(b)!,
            b => MemoryPackSerializer.Deserialize<TRow>(b)!
        );

    public ColdTable<TKey, TRow> OpenTable<TKey, TRow>(
        string name,
        Func<TKey, byte[]> serializeKey,
        Func<byte[], TKey> deserializeKey,
        Func<byte[], TRow> deserializeRow
    )
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct {
        if (tables.TryGetValue(name, out var existing)) return (ColdTable<TKey, TRow>)existing;

        var rcTxn = env.BeginTxn(0, out var txn);
        if (rcTxn != 0 || txn is null) throw MdbxErrorMapper.Map(rcTxn).ToException();

        var rcDbi = txn.OpenDbi(name, CreateDbi, out var dbi);
        if (rcDbi != 0) {
            txn.Abort();
            throw MdbxErrorMapper.Map(rcDbi).ToException();
        }

        var rcCommit = txn.Commit();
        if (rcCommit != 0) throw MdbxErrorMapper.Map(rcCommit).ToException();

        var table = new ColdTable<TKey, TRow>(dbi, serializeKey, deserializeKey, deserializeRow);
        tables[name] = table;
        tableDbisById[TableIdHash.Compute(name)] = dbi;
        return table;
    }

    public Result<int> ReadGeneration() => checkpoint.ReadGeneration();

    public Result<int> ReadRetainedFromGeneration() => checkpoint.ReadRetainedFromGeneration();

    public Result<int> PruneArchiveOlderThan(ulong cutoffUtcTicks) {
        var retained = ReadRetainedFromGeneration();
        if (retained.IsError()) return retained.Void();
        var currentFloor = retained.Unwrap();

        var deleted = WalArchive.DeleteSegmentsOlderThanTimestamp(DirectoryPath, cutoffUtcTicks);
        if (deleted.IsError()) return deleted.Void();

        var oldestSurviving = WalArchive.ReadOldestRetainedGeneration(DirectoryPath);
        if (oldestSurviving.IsError()) return oldestSurviving.Void();
        var newFloor = oldestSurviving.Unwrap().TryGet(out var oldestGeneration) && oldestGeneration > currentFloor
            ? oldestGeneration
            : currentFloor;

        var written = WriteRetainedFromGeneration(newFloor);
        if (written.IsError()) return Result<int>.Error(written.GetError());

        return deleted.Unwrap();
    }

    public Result<int> PruneArchiveOlderThanGeneration(int targetGeneration) {
        var retained = ReadRetainedFromGeneration();
        if (retained.IsError()) return retained.Void();

        if (targetGeneration < retained.Unwrap()) return Result<int>.Error(DbError.RetentionFloorCannotMoveBackward());

        var deleted = WalArchive.DeleteSegmentsOlderThan(DirectoryPath, targetGeneration);
        if (deleted.IsError()) return deleted.Void();

        var written = WriteRetainedFromGeneration(targetGeneration);
        if (written.IsError()) return Result<int>.Error(written.GetError());

        return deleted.Unwrap();
    }

    public Result<List<WalSegmentDescription>> DescribeArchiveSegments() => WalArchive.DescribeSegments(DirectoryPath);
    
    public Result WriteRetainedFromGeneration(int generation) {
        var rc = env.BeginTxn(0, out var txn);
        if (rc != 0 || txn is null) return Result.Error(MdbxErrorMapper.Map(rc));
        using var _ = txn;

        var writeResult = checkpoint.WriteRetainedFromGeneration(txn, generation);
        if (writeResult.IsError()) return writeResult;

        var commitRc = txn.Commit();
        return commitRc != 0 ? Result.Error(MdbxErrorMapper.Map(commitRc)) : Result.Ok();
    }

    public Result<TRow> Peek<TKey, TRow>(ColdTable<TKey, TRow> table, TKey key)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct {
        var rc = env.BeginTxn(ReadOnlyTxn, out var txn);
        if (rc != 0 || txn is null) return Result<TRow>.Error(MdbxErrorMapper.Map(rc));

        using var _ = txn;
        return table.Get(txn, key);
    }

    public IEnumerable<(TKey Key, TRow Row)> ScanAll<TKey, TRow>(ColdTable<TKey, TRow> table)
        where TKey : IEquatable<TKey>, IComparable<TKey>
        where TRow : struct {
        var rc = env.BeginTxn(ReadOnlyTxn, out var txn);
        if (rc != 0 || txn is null) throw MdbxErrorMapper.Map(rc).ToException();

        using var _ = txn;
        foreach (var pair in table.ScanAll(txn)) yield return pair;
    }

    public Result RunMigration(
        int targetGeneration,
        IReadOnlyList<(string TableName, Func<byte[], byte[], (byte[] Key, byte[] Row)> Transform)> tableRewrites,
        IReadOnlyList<string>? orphanTablesToDrop = null
    ) {
        var rc = env.BeginTxn(0, out var txn);
        if (rc != 0 || txn is null) return Result.Error(MdbxErrorMapper.Map(rc));

        using var _ = txn;

        foreach (var (tableName, transform) in tableRewrites) {
            var rewriteResult = RewriteTableRaw(txn, tableName, transform);
            if (rewriteResult.IsError()) return rewriteResult;
        }

        foreach (var tableName in orphanTablesToDrop ?? []) {
            var dropResult = DropTableEntirely(txn, tableName);
            if (dropResult.IsError()) return dropResult;
        }

        var writeGenerationResult = checkpoint.WriteGeneration(txn, targetGeneration);
        if (writeGenerationResult.IsError()) return writeGenerationResult;

        var commitRc = txn.Commit();
        return commitRc != 0 ? Result.Error(MdbxErrorMapper.Map(commitRc)) : Result.Ok();
    }

    private Result DropTableEntirely(Transaction txn, string tableName) {
        var rcOpen = txn.OpenDbi(tableName, CreateDbi, out var dbi);
        if (rcOpen != 0) return Result.Error(MdbxErrorMapper.Map(rcOpen));

        var rcDrop = txn.Drop(dbi, del: true);
        if (rcDrop != 0) return Result.Error(MdbxErrorMapper.Map(rcDrop));

        tableDbisById.Remove(TableIdHash.Compute(tableName));
        tables.Remove(tableName);
        return Result.Ok();
    }

    private Result RewriteTableRaw(Transaction txn, string tableName, Func<byte[], byte[], (byte[] Key, byte[] Row)> transform) {
        var scratchName = $"{tableName}__migrate_scratch";

        var rcScratch = txn.OpenDbi(scratchName, CreateDbi, out var scratchDbi);
        if (rcScratch != 0) return Result.Error(MdbxErrorMapper.Map(rcScratch));

        var rcOld = txn.OpenDbi(tableName, CreateDbi, out var oldDbi);
        if (rcOld != 0) return Result.Error(MdbxErrorMapper.Map(rcOld));

        var copyToScratchResult = CopyAllRows(txn, oldDbi, scratchDbi, transform);
        if (copyToScratchResult.IsError()) return copyToScratchResult;

        var rcDropOld = txn.Drop(oldDbi, del: true);
        if (rcDropOld != 0) return Result.Error(MdbxErrorMapper.Map(rcDropOld));

        var rcReopen = txn.OpenDbi(tableName, CreateDbi, out var newDbi);
        if (rcReopen != 0) return Result.Error(MdbxErrorMapper.Map(rcReopen));

        var copyBackResult = CopyAllRows(txn, scratchDbi, newDbi, static (key, row) => (key, row));
        if (copyBackResult.IsError()) return copyBackResult;

        var rcDropScratch = txn.Drop(scratchDbi, del: true);
        if (rcDropScratch != 0) return Result.Error(MdbxErrorMapper.Map(rcDropScratch));

        tableDbisById[TableIdHash.Compute(tableName)] = newDbi;
        if (tables.TryGetValue(tableName, out var cached) && cached is IColdTableDbiHandle handle) handle.UpdateDbi(newDbi);

        return Result.Ok();
    }

    static private Result CopyAllRows(Transaction txn, uint sourceDbi, uint destDbi, Func<byte[], byte[], (byte[] Key, byte[] Row)> transform) {
        var rcCursor = txn.OpenCursor(sourceDbi, out var cursor);
        if (rcCursor != 0) return Result.Error(MdbxErrorMapper.Map(rcCursor));

        using (cursor) {
            var getRc = cursor!.GetFirst(out var keyBytes, out var rowBytes);
            while (getRc == 0) {
                var (newKey, newRow) = transform(keyBytes, rowBytes);
                var putRc = txn.Put(destDbi, newKey, newRow, 0);
                if (putRc != 0) return Result.Error(MdbxErrorMapper.Map(putRc));
                getRc = cursor.GetNext(out keyBytes, out rowBytes);
            }
        }

        return Result.Ok();
    }

    public void Dispose() {
        Wal.Dispose();
        env.Dispose();
    }
}
