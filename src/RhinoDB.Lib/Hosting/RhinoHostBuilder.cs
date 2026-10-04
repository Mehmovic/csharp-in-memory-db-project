using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Hosting;

public delegate Task<Result<ReadOnlyMemory<byte>>> RpcCommandHandler(RhinoHost host, ReadOnlyMemory<byte> body, CancellationToken ct);

public sealed record RestRequest(string Method, string Route, IReadOnlyDictionary<string, string> Query, ReadOnlyMemory<byte> Body);
public sealed record RestResponse(int StatusCode, ReadOnlyMemory<byte> Body) {
    static public RestResponse Ok(ReadOnlyMemory<byte> body) => new RestResponse(200, body);
}
public delegate Task<RestResponse> RestCommandHandler(RhinoHost host, RestRequest request, CancellationToken ct);

public sealed class RhinoHostBuilder {
    private readonly string configDirectory;
    private IDatabaseRegistration? registration;
    private readonly Dictionary<Type, object> tableCompatAdapters = [];
    private readonly Dictionary<uint, RpcCommandHandler> rpcCommands = [];
    private readonly Dictionary<(string Method, string Route), RestCommandHandler> restCommands = [];
    public event Action<ArchiveRetentionRunReport>? OnRetentionRun;

    private RhinoHostBuilder(string configDirectory) {
        this.configDirectory = configDirectory;
    }

    static public RhinoHostBuilder Create(string? configDirectory = null) =>
        new RhinoHostBuilder(configDirectory ?? AppContext.BaseDirectory);

    public RhinoHostBuilder AddDatabase<TDb, TTx>(Action<DatabaseOptions<TDb, TTx>> configure)
        where TDb : DbContext<TTx>
        where TTx : ITransaction {
        if (registration is not null)
            throw new InvalidOperationException("AddDatabase was already called - a RhinoHostBuilder hosts exactly one database.");

        var options = new DatabaseOptions<TDb, TTx>();
        configure(options);
        registration = new DatabaseRegistration<TDb, TTx>(options);
        return this;
    }

    public RhinoHostBuilder AddTableCompatAdapter<TRow>(Action<TableCompatOptions<TRow>> configure) {
        var options = new TableCompatOptions<TRow>();
        configure(options);
        tableCompatAdapters[typeof(TRow)] = options;
        return this;
    }

    public RhinoHostBuilder AddRpcCommand(uint commandHash, RpcCommandHandler handler) {
        return !rpcCommands.TryAdd(commandHash, handler) ? throw new ArgumentException($"An RPC command with hash {commandHash} is already registered.", nameof(commandHash)) : this;
    }

    public RhinoHostBuilder AddRestCommand(string method, string route, RestCommandHandler handler) {
        return !restCommands.TryAdd((method, route), handler)
            ? throw new ArgumentException($"A REST command for {method} {route} is already registered.", nameof(route))
            : this;
    }

    public async Task<Result<RhinoHost>> BuildAsync() {
        if (registration is not { } reg)
            return Result<RhinoHost>.Error(DbError.SystemFailure(
                new InvalidOperationException("BuildAsync was called without AddDatabase ever being called.")));

        var result = await reg.RunAsync(configDirectory);
        if (result.IsError()) return result.Void();
        var builtDb = result.Unwrap();

        IDisposable? collector = null;
        var policy = reg.ArchiveRetentionOverride ?? reg.ConfiguredRetention();
        if (policy is not null)
            collector = reg.StartArchiveCollector(builtDb, policy, report => OnRetentionRun?.Invoke(report));

        var host = new RhinoHost(builtDb, tableCompatAdapters, rpcCommands, restCommands, collector);

        TryLoadNetworkHostProvider();
        if (reg.HttpEnabled && RhinoNetworkHostProvider.Factory is { } factory) {
            var networkResult = await factory.StartAsync(host, reg.HttpPort, CancellationToken.None);
            if (networkResult.IsError()) {
                host.Dispose();
                return networkResult.Void();
            }
            host.AttachNetworkHost(networkResult.Unwrap());
        }

        return host;
    }

    static private void TryLoadNetworkHostProvider() {
        if (RhinoNetworkHostProvider.Factory is not null) return;
        try {
            var assembly = System.Reflection.Assembly.Load("RhinoDB.Lib.Server");
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        } catch (FileNotFoundException) { /* not referenced - no network host */ }
    }

    private interface IDatabaseRegistration {
        Task<Result<object>> RunAsync(string configDirectory);
        ArchiveRetentionPolicy? ArchiveRetentionOverride { get; }
        ArchiveRetentionPolicy? ConfiguredRetention();
        IDisposable? StartArchiveCollector(object builtDbParam, ArchiveRetentionPolicy policy, Action<ArchiveRetentionRunReport> onRun);
        bool HttpEnabled { get; }
        int HttpPort { get; }
    }

    private sealed class DatabaseRegistration<TDb, TTx>(DatabaseOptions<TDb, TTx> options)
        : IDatabaseRegistration
        where TDb : DbContext<TTx>
        where TTx : ITransaction {
        private TDb? builtDb;
        private RhinoHostOptions? parsedOptions;
        public ArchiveRetentionPolicy? ArchiveRetentionOverride { get; } = options.ArchiveRetention;
        public bool HttpEnabled => parsedOptions?.HttpEnabled ?? false;
        public int HttpPort => parsedOptions?.HttpPort ?? 0;

        public ArchiveRetentionPolicy? ConfiguredRetention() => builtDb?.ConfiguredArchiveRetention;

        public IDisposable? StartArchiveCollector(object builtDbParam, ArchiveRetentionPolicy policy, Action<ArchiveRetentionRunReport> onRun) {
            return new ArchiveRetentionCollector<TTx>((DbContext<TTx>)builtDbParam, policy, onRun);
        }

        public async Task<Result<object>> RunAsync(string configDirectory) {
            if (options.CreateDb is not { } createDb)
                return Result<object>.Error(MissingConfig(nameof(options.CreateDb), because: "every mode needs it"));

            var parsedResult = RhinoHostOptions.Load(configDirectory);
            if (parsedResult.IsError()) return parsedResult.Void();
            var parsed = parsedResult.Unwrap();
            parsedOptions = parsed;

            switch (parsed.Mode) {
                case RhinoRunMode.Replay when options.LoadFromGenesis is null:
                    return Result<object>.Error(MissingConfig(nameof(options.LoadFromGenesis), because: "RhinoRunMode.Replay needs it"));
                case RhinoRunMode.Migrate when options.RunMigration is null:
                    return Result<object>.Error(MissingConfig(nameof(options.RunMigration), because: "RhinoRunMode.Migrate needs it"));
                case RhinoRunMode.WalPrune when parsed.WalKeepGenerations is null && parsed.WalPruneOlderThanUtcTicks is null:
                    return Result<object>.Error(MissingPruneConfig());
                case RhinoRunMode.WalMigrate when options.MigrateWalArchive is null:
                    return Result<object>.Error(MissingConfig(nameof(options.MigrateWalArchive), because: "RhinoRunMode.WalMigrate needs it"));
            }

            var runResult = await RunOne<TDb, TTx>(
                parsed, createDb, options.LoadFromGenesis,
                options.RunMigration, options.GBinary, options.IsGenerationInvalid, options.MigrateWalArchive);
            if (runResult.IsError()) return runResult.Void();

            builtDb = runResult.Unwrap();
            return Result<object>.Ok(builtDb);
        }

        private DbError MissingConfig(string member, string because) =>
            DbError.SystemFailure(new InvalidOperationException($"AddDatabase: {member} was not configured - {because}."));

        private DbError MissingPruneConfig() =>
            DbError.SystemFailure(
                new InvalidOperationException(
                    "AddDatabase: RhinoRunMode.WalPrune needs either Host.WalKeepGenerations (keep the last N generations) or Host.WalPruneOlderThan=<ISO-8601> (keep everything newer than a moment in time) set in rdbsettings.json."
                )
            );
    }

    static private async Task<Result<TDb>> RunOne<TDb, TTx>(
        RhinoHostOptions options,
        Func<ColdStore, TDb> createDb,
        Func<TDb, ColdStore, ulong?, Result>? loadFromGenesis,
        Func<TDb, Result>? runMigration,
        int? binaryGeneration,
        Func<int, bool>? isGenerationInvalid,
        Func<TDb, Result>? migrateWalArchive
    ) where TDb : DbContext<TTx> where TTx : ITransaction {
        var coldResult = ColdStore.Open(options.ColdPath);
        if (coldResult.IsError()) return coldResult.Void();
        var cold = coldResult.Unwrap();

        var db = createDb(cold);

        if (cold.WasFreshlyCreated) {
            var initResult = await db.OnInitAsync();
            if (initResult.IsError()) {
                cold.Dispose();
                return initResult;
            }
        }

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
                var startResult = await db.OnStartAsync();
                if (startResult.IsError()) {
                    cold.Dispose();
                    return startResult;
                }
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
            case RhinoRunMode.WalPrune: {
                var pruneError = options.WalKeepGenerations is { } pruneGeneration
                    ? cold.PruneArchiveOlderThanGeneration(pruneGeneration)
                    : cold.PruneArchiveOlderThan(options.WalPruneOlderThanUtcTicks!.Value);

                if (pruneError.IsError()) {
                    cold.Dispose();
                    return pruneError.Void();
                }
                break;
            }
            case RhinoRunMode.WalMigrate: {
                var walMigrateResult = migrateWalArchive!(db);
                if (walMigrateResult.IsError()) {
                    cold.Dispose();
                    return walMigrateResult;
                }
                break;
            }
        }
        return db;
        }
    }

public sealed class RhinoHost : IDisposable {
    private readonly object database;
    private readonly IReadOnlyDictionary<Type, object> tableCompatAdapters;
    private readonly IReadOnlyDictionary<uint, RpcCommandHandler> rpcCommands;
    private readonly IReadOnlyDictionary<(string Method, string Route), RestCommandHandler> restCommands;
    private readonly IDisposable? collector;
    private IRhinoNetworkHost? networkHost;
    private bool disposed;

    internal RhinoHost(
        object database, IReadOnlyDictionary<Type, object> tableCompatAdapters,
        IReadOnlyDictionary<uint, RpcCommandHandler> rpcCommands,
        IReadOnlyDictionary<(string Method, string Route), RestCommandHandler> restCommands, IDisposable? collector) {
        this.database = database;
        this.tableCompatAdapters = tableCompatAdapters;
        this.rpcCommands = rpcCommands;
        this.restCommands = restCommands;
        this.collector = collector;
    }

    public TDb GetDatabase<TDb>() where TDb : notnull => (TDb)database;

    public TableCompatOptions<TRow>? GetTableCompatAdapter<TRow>() =>
        tableCompatAdapters.TryGetValue(typeof(TRow), out var options)
            ? (TableCompatOptions<TRow>)options
            : null;

    public async Task<Result<ReadOnlyMemory<byte>>> DispatchRpcAsync(uint commandHash, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (!rpcCommands.TryGetValue(commandHash, out var handler))
            return Result<ReadOnlyMemory<byte>>.Error(DbError.UnknownRpcCommand());
        return await handler(this, body, ct);
    }

    public IEnumerable<(string Method, string Route)> RegisteredRestCommands => restCommands.Keys;

    public async Task<RestResponse> DispatchRestAsync(string method, string route, RestRequest request, CancellationToken ct) {
        if (!restCommands.TryGetValue((method, route), out var handler)) return new RestResponse(404, ReadOnlyMemory<byte>.Empty);
        return await handler(this, request, ct);
    }

    public Task<Result> DispatchClientConnectAsync(Session session) => ((IRhinoClientLifecycle)database).OnClientConnectAsync(session);

    public Task<Result> DispatchClientDisconnectAsync(Session session) => ((IRhinoClientLifecycle)database).OnClientDisconnectAsync(session);

    public int ArchiveCollectorCount => collector is null ? 0 : 1;

    public int? NetworkPort => networkHost?.Port;

    internal void AttachNetworkHost(IRhinoNetworkHost host) => networkHost = host;

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        if (networkHost is not null) {
            try { networkHost.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* best-effort - shutdown must not throw */ }
        }
        if (collector is not null) {
            try { collector.Dispose(); } catch { /* best-effort - shutdown must not throw */ }
        }
    }
}
