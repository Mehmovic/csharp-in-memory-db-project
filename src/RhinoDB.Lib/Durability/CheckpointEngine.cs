using RhinoDB.Lib.Cold;
using RhinoDB.Native;

namespace RhinoDB.Lib.Durability;

public sealed class CheckpointEngine(MdbxEnvironment env, WriteAheadLog wal, string archiveDirectory) {
    private const string MetadataDbiName = "__rhinodb_checkpoint__";
    private const uint CreateDbi = 0x40000;
    private const int MdbxResultTrue = -1;
    static private readonly byte[] WatermarkKey = [0];

    private uint metadataDbi;
    private bool metadataDbiResolved;

    public Result<long> ReadCheckpointedLsn() {
        var rc = env.BeginTxn(0, out var txn);
        if (rc != 0 || txn is null) return Result<long>.Error(MdbxErrorMapper.Map(rc));
        using var _ = txn;

        var resolved = EnsureMetadataDbi(txn);
        if (resolved.IsError()) return Result<long>.Error(resolved.GetError());

        var getRc = txn.Get(metadataDbi, WatermarkKey, out var bytes);
        return getRc != 0 ? Result<long>.Ok(-1L) : Result<long>.Ok(BitConverter.ToInt64(bytes));
    }

    public async Task<Result> RunCheckpoint(
        long boundaryLsn,
        IReadOnlyDictionary<uint, uint> tableDbis,
        IEnumerable<CheckpointRow> residentRows,
        IEnumerable<(uint TableId, byte[] Key)> deletedSinceLastCheckpoint,
        DecodedWalEntry[] entries
    ) {
        var rcTxn = env.BeginTxn(0, out var txn);
        if (rcTxn != 0 || txn is null) return Result.Error(MdbxErrorMapper.Map(rcTxn));
        using var _ = txn;

        foreach (var row in residentRows) {
            if (!tableDbis.TryGetValue(row.TableId, out var dbi))
                return UnknownTableIdResult(row.TableId);

            var putRc = txn.Put(dbi, row.Key, row.Row, 0);
            if (putRc != 0) return Result.Error(MdbxErrorMapper.Map(putRc));
        }

        foreach (var (tableId, key) in deletedSinceLastCheckpoint) {
            if (!tableDbis.TryGetValue(tableId, out var dbi))
                return UnknownTableIdResult(tableId);

            var delRc = txn.Delete(dbi, key);
            if (delRc != 0 && MdbxErrorMapper.Map(delRc).Kind != ErrorKind.IndexKeyNotFound) return Result.Error(MdbxErrorMapper.Map(delRc));
        }

        var metadataResolved = EnsureMetadataDbi(txn);
        if (metadataResolved.IsError()) return metadataResolved;

        var watermarkRc = txn.Put(metadataDbi, WatermarkKey, BitConverter.GetBytes(boundaryLsn), 0);
        if (watermarkRc != 0) return Result.Error(MdbxErrorMapper.Map(watermarkRc));

        var commitRc = txn.Commit();
        if (commitRc != 0) return Result.Error(MdbxErrorMapper.Map(commitRc));

        var syncRc = env.Sync(force: true, nonblock: false);
        if (syncRc != 0 && syncRc != MdbxResultTrue) return Result.Error(MdbxErrorMapper.Map(syncRc));

        var archiveError = WalArchive.WriteSegment(archiveDirectory, wal.DatabaseId, entries, 0);

        var truncateError = await wal.Truncate();
        if (truncateError is { } err) return Result.Error(err);

        return archiveError is { } archiveErr ? Result.Error(archiveErr) : Result.Ok();

        static Result UnknownTableIdResult(uint tableId)
            => Result.Error(DbError.SystemFailure(new Exception($"Unknown tableId {tableId} in the RunCheckpoint()")));
    }

    private Result EnsureMetadataDbi(Transaction txn) {
        if (metadataDbiResolved) return Result.Ok();

        var rc = txn.OpenDbi(MetadataDbiName, CreateDbi, out var dbi);
        if (rc != 0) return Result.Error(MdbxErrorMapper.Map(rc));

        metadataDbi = dbi;
        metadataDbiResolved = true;
        return Result.Ok();
    }
}
