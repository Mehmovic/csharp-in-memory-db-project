using RhinoDB.Lib.Cold;
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
            }

            var runResult = await RunOne(parsed, createDb, options.LoadAsync, options.LoadFromGenesis);
            return runResult.IsError() ? runResult.Void() : Result<object>.Ok(runResult.Unwrap());
        }

        private DbError MissingConfig(string member, string because) =>
            DbError.SystemFailure(new InvalidOperationException($"AddDatabase(\"{Name}\"): {member} was not configured - {because}."));
    }

    static private async Task<Result<TDb>> RunOne<TDb>(
        RhinoHostOptions options,
        Func<ColdStore, TDb> createDb,
        Func<TDb, Task>? loadAsync,
        Func<TDb, ColdStore, long?, Result>? loadFromGenesis
    ) where TDb : notnull {
        var coldResult = ColdStore.Open(options.ColdPath);
        if (coldResult.IsError()) return coldResult.Void();
        var cold = coldResult.Unwrap();

        var db = createDb(cold);

        switch (options.Mode) {
            case RhinoRunMode.Run: {
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
            case RhinoRunMode.Migrate:
                cold.Dispose();
                return Result<TDb>.Error(
                    DbError.SystemFailure(
                        new NotSupportedException("Migration is not built yet - see Docs/06-schema-migration.md.")
                    )
                );
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
