using System.Collections.Concurrent;

using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Lib.Hosting;

public sealed class ChildDatabaseOptions<TChildDb, TTx, TKey>
    where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
    public Func<ColdStore, TChildDb>? CreateDb { get; set; }
    public Func<TKey, string>? KeyToDirectoryName { get; set; }
}

internal interface IChildDatabaseRegistry {
    void AttachRootColdPath(string rootColdPath);
    void CloseAllBestEffort();
}

internal sealed class ChildDatabaseRegistry<TChildDb, TTx, TKey>(ChildDatabaseOptions<TChildDb, TTx, TKey> options)
    : IChildDatabaseRegistry
    where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
    private readonly ConcurrentDictionary<TKey, TChildDb> active = new ConcurrentDictionary<TKey, TChildDb>();
    private string? baseDirectory;

    public void AttachRootColdPath(string rootColdPath) =>
        baseDirectory = Path.Combine(rootColdPath, "Children", typeof(TChildDb).Name);

    public async Task<Result<TChildDb>> GetOrActivateAsync(TKey key, CancellationToken ct) {
        if (active.TryGetValue(key, out var existing)) return existing;
        if (options.CreateDb is not { } createDb)
            return Result<TChildDb>.Error(DbError.SystemFailure(new InvalidOperationException(
                $"AddChildDatabase<{typeof(TChildDb).Name}>: CreateDb was not configured.")));

        var dir = Path.Combine(baseDirectory!, DirectoryNameFor(key));
        Directory.CreateDirectory(dir);
        var coldResult = ColdStore.Open(dir);
        if (coldResult.IsError()) return coldResult.Void();
        var cold = coldResult.Unwrap();

        var db = createDb(cold);
        if (cold.WasFreshlyCreated) {
            var initResult = await db.OnInitAsync();
            if (initResult.IsError()) { cold.Dispose(); return initResult; }
        }
        var startResult = await db.OnStartAsync();
        if (startResult.IsError()) { cold.Dispose(); return startResult; }

        if (active.TryAdd(key, db)) return db;
        cold.Dispose();
        return active.GetValueOrDefault(key, db);
    }

    public async Task<Result> DisposeAsync(TKey key, CancellationToken drainCt) {
        if (!active.TryGetValue(key, out var db)) return Result.Ok();

        db.BeginDraining();
        try {
            await db.DrainAsync().WaitAsync(drainCt);
        } catch (OperationCanceledException) {
            return Result.Error(DbError.SystemFailure(new TimeoutException(
                $"Draining child database '{typeof(TChildDb).Name}' key '{key}' did not finish before the "
                + "caller-supplied CancellationToken fired - the child was NOT disposed and its data was NOT "
                + "deleted; it is still draining and will reject new work - retry DisposeChildAsync to finish.")));
        }

        active.TryRemove(key, out _);
        db.Cold?.Dispose();
        try { Directory.Delete(Path.Combine(baseDirectory!, DirectoryNameFor(key)), recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
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
