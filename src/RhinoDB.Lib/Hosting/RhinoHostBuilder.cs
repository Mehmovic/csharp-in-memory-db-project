using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Lib.Hosting;

public sealed class RhinoHostBuilder {
    private readonly string[] args;
    private readonly List<IDatabaseRegistration> registrations = [];
    private readonly HashSet<string> names = [];

    private RhinoHostBuilder(string[] args) {
        this.args = args;
    }

    static public RhinoHostBuilder Create(string[] args) => new RhinoHostBuilder(args);

    public RhinoHostBuilder AddDatabase<TDb, TTx>(string name, Action<DatabaseOptions<TDb, TTx>> configure)
        where TDb : DbContext<TTx>
        where TTx : ITransaction {
        if (!names.Add(name))
            throw new ArgumentException($"A database named '{name}' is already registered.", nameof(name));

        var options = new DatabaseOptions<TDb, TTx>();
        configure(options);
        registrations.Add(new DatabaseRegistration<TDb, TTx>(name, options));
        return this;
    }

    public async Task<Result<RhinoHost>> BuildAsync() {
        var databases = new Dictionary<string, object>();

        foreach (var registration in registrations) {
            var result = await registration.RunAsync(args);
            if (result.IsError()) return result.Void();

            databases[registration.Name] = result.Unwrap();
        }

        return new RhinoHost(databases);
    }

    private interface IDatabaseRegistration {
        string Name { get; }
        Task<Result<object>> RunAsync(string[] args);
    }

    private sealed class DatabaseRegistration<TDb, TTx>(string name, DatabaseOptions<TDb, TTx> options)
        : IDatabaseRegistration
        where TDb : DbContext<TTx>
        where TTx : ITransaction {
        public string Name { get; } = name;

        public async Task<Result<object>> RunAsync(string[] args) {
            if (options.CreateDb is not { } createDb)
                return Result<object>.Error(MissingConfig(nameof(options.CreateDb), because: "every mode needs it"));

            var parsedResult = RhinoHostOptions.Parse(args, Name);
            if (parsedResult.IsError()) return parsedResult.Void();
            var parsed = parsedResult.Unwrap();

            switch (parsed.Mode) {
                case RhinoRunMode.Run when options.LoadAsync is null:
                    return Result<object>.Error(MissingConfig(nameof(options.LoadAsync), because: "RhinoRunMode.Run needs it"));
                case RhinoRunMode.Replay when options.LoadFromGenesis is null:
                    return Result<object>.Error(MissingConfig(nameof(options.LoadFromGenesis), because: "RhinoRunMode.Replay needs it"));
                case RhinoRunMode.Migrate when options.RunMigration is null:
                    return Result<object>.Error(MissingConfig(nameof(options.RunMigration), because: "RhinoRunMode.Migrate needs it"));
                case RhinoRunMode.Prune when parsed.PruneTargetGeneration is null:
                    return Result<object>.Error(DbError.SystemFailure(new InvalidOperationException(
                        $"AddDatabase(\"{Name}\"): --{Name}.prune-target-generation is required for RhinoRunMode.Prune.")));
                case RhinoRunMode.ConsolidateArchive when options.ConsolidateArchive is null:
                    return Result<object>.Error(MissingConfig(nameof(options.ConsolidateArchive), because: "RhinoRunMode.ConsolidateArchive needs it"));
            }

            var runResult = await RunOne(
                parsed, createDb, options.LoadAsync, options.LoadFromGenesis,
                options.RunMigration, options.GBinary, options.IsGenerationInvalid, options.ConsolidateArchive);
            return runResult.IsError() ? runResult.Void() : Result<object>.Ok(runResult.Unwrap());
        }

        private DbError MissingConfig(string member, string because) =>
            DbError.SystemFailure(new InvalidOperationException($"AddDatabase(\"{Name}\"): {member} was not configured - {because}."));
    }

    static private async Task<Result<TDb>> RunOne<TDb>(
        RhinoHostOptions options,
        Func<ColdStore, TDb> createDb,
        Func<TDb, Task>? loadAsync,
        Func<TDb, ColdStore, long?, Result>? loadFromGenesis,
        Func<TDb, Result>? runMigration,
        int? binaryGeneration,
        Func<int, bool>? isGenerationInvalid,
        Func<TDb, Result>? consolidateArchive
    ) where TDb : notnull {
        var coldResult = ColdStore.Open(options.ColdPath);
        if (coldResult.IsError()) return coldResult.Void();
        var cold = coldResult.Unwrap();

        var db = createDb(cold);

        switch (options.Mode) {
            case RhinoRunMode.Run: {
                if (binaryGeneration is { } gBinary && isGenerationInvalid is not null && runMigration is not null) {
                    var currentGenerationResult = cold.ReadGeneration();
                    if (currentGenerationResult.IsError()) {
                        cold.Dispose();
                        return currentGenerationResult.Void();
                    }
                    var currentGeneration = currentGenerationResult.Unwrap();

                    var decisionResult = SchemaGenerationCheck.EnsureCurrentGeneration(
                        currentGeneration, cold.WalGeneration, gBinary,
                        migrationChainExists: true, isGenerationInvalid(currentGeneration));
                    if (decisionResult.IsError()) {
                        cold.Dispose();
                        return decisionResult.Void();
                    }

                    if (decisionResult.Unwrap() == SchemaGenerationDecision.MigrationRequired) {
                        var migrateResult = runMigration(db);
                        if (migrateResult.IsError()) {
                            cold.Dispose();
                            return migrateResult;
                        }
                    }
                }

                var recoveryResult = await cold.CompleteRecoveryAsync();
                if (recoveryResult.IsError()) {
                    cold.Dispose();
                    return recoveryResult;
                }
                await loadAsync!(db);
                break;
            }
            case RhinoRunMode.Replay: {
                var replayResult = loadFromGenesis!(db, cold, options.ReplayUpToLsn);
                if (replayResult.IsError()) {
                    cold.Dispose();
                    return replayResult;
                }
                break;
            }
            case RhinoRunMode.Migrate: {
                var migrateResult = runMigration!(db);
                if (migrateResult.IsError()) {
                    cold.Dispose();
                    return migrateResult;
                }
                break;
            }
            case RhinoRunMode.Prune: {
                var targetGeneration = options.PruneTargetGeneration!.Value;

                var retainedFromGenerationResult = cold.ReadRetainedFromGeneration();
                if (retainedFromGenerationResult.IsError()) {
                    cold.Dispose();
                    return retainedFromGenerationResult.Void();
                }
                if (targetGeneration < retainedFromGenerationResult.Unwrap()) {
                    cold.Dispose();
                    return Result<TDb>.Error(DbError.RetentionFloorCannotMoveBackward());
                }

                var deleteResult = WalArchive.DeleteSegmentsOlderThan(cold.DirectoryPath, targetGeneration);
                if (deleteResult.IsError()) {
                    cold.Dispose();
                    return deleteResult;
                }

                var writeFloorResult = cold.WriteRetainedFromGeneration(targetGeneration);
                if (writeFloorResult.IsError()) {
                    cold.Dispose();
                    return writeFloorResult;
                }
                break;
            }
            case RhinoRunMode.ConsolidateArchive: {
                var consolidateResult = consolidateArchive!(db);
                if (consolidateResult.IsError()) {
                    cold.Dispose();
                    return consolidateResult;
                }
                break;
            }
        }

        return db;
    }
}

public sealed class RhinoHost {
    private readonly IReadOnlyDictionary<string, object> databases;

    internal RhinoHost(IReadOnlyDictionary<string, object> databases) {
        this.databases = databases;
    }

    public TDb GetDatabase<TDb>(string name) where TDb : notnull =>
        databases.TryGetValue(name, out var db)
            ? (TDb)db
            : throw new KeyNotFoundException($"No database registered under '{name}'.");
}
