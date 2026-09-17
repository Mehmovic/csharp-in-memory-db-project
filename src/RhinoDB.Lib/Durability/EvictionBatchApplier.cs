using RhinoDB.Lib.Cold;
using RhinoDB.Native;

namespace RhinoDB.Lib.Durability;

static public class EvictionBatchApplier {
    private const int MdbxResultTrue = -1;

    static public Result<IReadOnlyList<EvictionCandidate>> Apply(
        MdbxEnvironment env,
        IReadOnlyDictionary<uint, uint> tableDbis,
        IReadOnlyList<EvictionCandidate> candidates,
        Func<uint, byte[], byte[]?> readCurrentValue,
        List<EvictionCandidate>? appliedInto = null) {
        var rcTxn = env.BeginTxn(0, out var txn);
        if (rcTxn != 0 || txn is null) return Result<IReadOnlyList<EvictionCandidate>>.Error(MdbxErrorMapper.Map(rcTxn));
        using var _ = txn;

        var applied = appliedInto ?? [];
        applied.Clear();
        foreach (var candidate in candidates) {
            var currentValue = readCurrentValue(candidate.TableId, candidate.Key);
            if (currentValue is null) continue;
            if (!tableDbis.TryGetValue(candidate.TableId, out var dbi)) continue;

            var putRc = txn.Put(dbi, candidate.Key, currentValue, 0);
            if (putRc != 0) return Result<IReadOnlyList<EvictionCandidate>>.Error(MdbxErrorMapper.Map(putRc));
            applied.Add(candidate);
        }

        var commitRc = txn.Commit();
        if (commitRc != 0) return Result<IReadOnlyList<EvictionCandidate>>.Error(MdbxErrorMapper.Map(commitRc));

        var syncRc = env.Sync(force: true, nonblock: false);
        if (syncRc != 0 && syncRc != MdbxResultTrue) return Result<IReadOnlyList<EvictionCandidate>>.Error(MdbxErrorMapper.Map(syncRc));

        return Result<IReadOnlyList<EvictionCandidate>>.Ok(applied);
    }
}
