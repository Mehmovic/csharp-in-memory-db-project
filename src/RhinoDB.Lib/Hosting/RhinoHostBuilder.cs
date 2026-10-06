using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Prefs;
using RhinoDB.Lib.Procedures;
using RhinoDB.Lib.Realtime;

namespace RhinoDB.Lib.Hosting;

public sealed record RestRequest(string Method, string Route, IReadOnlyDictionary<string, string> Query, ReadOnlyMemory<byte> Body);
public sealed record RestResponse(int StatusCode, ReadOnlyMemory<byte> Body) {
    static public RestResponse Ok(ReadOnlyMemory<byte> body) => new RestResponse(200, body);
}
public delegate Task<RestResponse> RestCommandHandler(RhinoHost host, RestRequest request, CancellationToken ct);

public sealed class RhinoHostBuilder {
    private readonly string configDirectory;
    private IDatabaseRegistration? registration;
    private readonly Dictionary<Type, object> tableCompatAdapters = [];
    private readonly Dictionary<uint, ProcedureDescriptor> procedures = [];
    private readonly Dictionary<(string Method, string Route), RestCommandHandler> restCommands = [];
    private readonly Dictionary<Type, IChildDatabaseRegistry> childRegistrations = [];
    private Func<uint, bool>? isAppVersionInvalid;
    private UnrecoverableErrorPolicy unrecoverableErrorPolicy = UnrecoverableErrorPolicy.ExitProcess;
    private Action<ProcedureFault> onProcedureFault = ProcedureFaults.WriteToStandardError;
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

    public RhinoHostBuilder AddProcedure(ProcedureDescriptor procedure) {
        return !procedures.TryAdd(procedure.Hash, procedure)
            ? throw new ArgumentException($"A procedure with hash {procedure.Hash} is already registered ('{procedures[procedure.Hash].Name}').", nameof(procedure))
            : this;
    }

    public RhinoHostBuilder OnProcedureFault(Action<ProcedureFault> handler) {
        onProcedureFault = handler;
        return this;
    }

    public RhinoHostBuilder AddRestCommand(string method, string route, RestCommandHandler handler) {
        return !restCommands.TryAdd((method, route), handler)
            ? throw new ArgumentException($"A REST command for {method} {route} is already registered.", nameof(route))
            : this;
    }

    public RhinoHostBuilder OnUnrecoverableError(UnrecoverableErrorPolicy policy) {
        unrecoverableErrorPolicy = policy;
        return this;
    }

    public RhinoHostBuilder SetAppVersionValidator(Func<uint, bool> validator) {
        isAppVersionInvalid = validator;
        return this;
    }

    public RhinoHostBuilder AddChildDatabase<TChildDb, TTx, TKey>(Action<ChildDatabaseOptions<TChildDb, TTx, TKey>> configure)
        where TChildDb : DbContext<TTx>
        where TTx : ITransaction
        where TKey : notnull {
        var options = new ChildDatabaseOptions<TChildDb, TTx, TKey>();
        configure(options);
        return !childRegistrations.TryAdd(typeof(TChildDb), new ChildDatabaseRegistry<TChildDb, TTx, TKey>(options))
            ? throw new ArgumentException($"AddChildDatabase<{typeof(TChildDb).Name}> was already called - each child database type can only be registered once.", nameof(TChildDb))
            : this;
    }

    public RhinoHostBuilder AddSingletonChildDatabase<TChildDb, TTx>(Action<SingletonChildDatabaseOptions<TChildDb, TTx>> configure)
        where TChildDb : DbContext<TTx>
        where TTx : ITransaction {
        var options = new SingletonChildDatabaseOptions<TChildDb, TTx>();
        configure(options);
        var keyed = new ChildDatabaseOptions<TChildDb, TTx, string> { CreateDb = options.CreateDb, Load = options.Load };
        return !childRegistrations.TryAdd(typeof(TChildDb), new ChildDatabaseRegistry<TChildDb, TTx, string>(keyed, isSingleton: true))
            ? throw new ArgumentException($"AddSingletonChildDatabase<{typeof(TChildDb).Name}> was already called - each child database type can only be registered once.", nameof(TChildDb))
            : this;
    }

    public async Task<Result<RhinoHost>> BuildAsync() {
        if (registration is not { } reg)
            return Result<RhinoHost>.Error(DbError.SystemFailure(
                new InvalidOperationException("BuildAsync was called without AddDatabase ever being called.")));

        var result = await reg.RunAsync(configDirectory);
        if (result.IsError()) return result.Void();
        var builtDb = result.Unwrap();

        var unrecoverableErrorHandler = new UnrecoverableErrorHandler(
            unrecoverableErrorPolicy, shutdownBudget: reg.Options.UnrecoverableShutdownBudget, exitWatchdog: reg.Options.UnrecoverableExitWatchdog);
        var rootName = $"{builtDb.GetType().Name} (Root)";
        ((IHostedDatabase)builtDb).OnPoisoned = error => unrecoverableErrorHandler.Trigger(rootName, error);

        foreach (var childRegistration in childRegistrations.Values) {
            childRegistration.AttachRootColdPath(reg.ColdPath, reg.Options.ColdStore);
            if (reg.ChainLog is { } chainLog) childRegistration.AttachChainLog(chainLog);
            childRegistration.AttachUnrecoverableErrorHandler(unrecoverableErrorHandler);
        }

        // Singletons live like the Root: recovered and started with it, so no request ever pays for activation.
        if (reg.Mode == RhinoRunMode.Run) {
            foreach (var singleton in childRegistrations.Values.Where(r => r.IsSingleton)) {
                var activated = await singleton.ActivateSingletonAsync();
                if (activated.IsError()) {
                    foreach (var childRegistration in childRegistrations.Values) childRegistration.CloseAllBestEffort();
                    return Result<RhinoHost>.Error(activated.GetError());
                }
            }
        }

        IDisposable? collector = null;
        var policy = reg.ArchiveRetentionOverride ?? reg.ConfiguredRetention();
        if (policy is not null)
            collector = reg.StartArchiveCollector(builtDb, policy, report => OnRetentionRun?.Invoke(report));

        var host = new RhinoHost(builtDb, tableCompatAdapters, procedures, onProcedureFault, restCommands, childRegistrations, isAppVersionInvalid ?? (_ => false), reg.ServerVersion, reg.ChainLog, collector);
        unrecoverableErrorHandler.Shutdown = host.ShutdownForUnrecoverableErrorAsync;

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
        string ColdPath { get; }
        RhinoRunMode Mode { get; }
        uint ServerVersion { get; }
        ChainLog? ChainLog { get; }
        RhinoHostOptions Options { get; }
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
        public uint ServerVersion => parsedOptions?.ServerVersion ?? 0;
        public RhinoRunMode Mode => parsedOptions?.Mode ?? RhinoRunMode.Run;
        public ChainLog? ChainLog { get; private set; }
        public RhinoHostOptions Options => parsedOptions ?? throw new InvalidOperationException("Options were read before RunAsync parsed them.");
        public string ColdPath => parsedOptions?.ColdPath ?? throw new InvalidOperationException("ColdPath was read before RunAsync resolved it.");

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

            var chainLogResult = ChainLog.Open(parsed.ColdPath);
            if (chainLogResult.IsError()) return chainLogResult.Void();
            ChainLog = chainLogResult.Unwrap();

            var runResult = await RunOne<TDb, TTx>(
                parsed, createDb, options.LoadFromGenesis,
                options.RunMigration, options.GBinary, options.IsGenerationInvalid, options.MigrateWalArchive,
                ChainLog.ResolverFor(ChainLog.RootParticipantId));
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
        Func<TDb, Result>? migrateWalArchive,
        IChainResolver chainResolver
    ) where TDb : DbContext<TTx> where TTx : ITransaction {
        var coldResult = ColdStore.Open(options.ColdPath, options.ColdStore);
        if (coldResult.IsError()) return coldResult.Void();
        var cold = coldResult.Unwrap();
        cold.AttachChainResolver(chainResolver);

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
    private readonly IReadOnlyDictionary<uint, ProcedureDescriptor> procedures;
    private readonly Action<ProcedureFault> onProcedureFault;
    private readonly IReadOnlyDictionary<(string Method, string Route), RestCommandHandler> restCommands;
    private readonly IReadOnlyDictionary<Type, IChildDatabaseRegistry> childRegistrations;
    private readonly Func<uint, bool> isAppVersionInvalid;
    private readonly uint serverVersion;
    private readonly ChainLog? chainLog;
    private readonly IDisposable? collector;
    private IRhinoNetworkHost? networkHost;
    private bool disposed;

    internal RhinoHost(
        object database, IReadOnlyDictionary<Type, object> tableCompatAdapters,
        IReadOnlyDictionary<uint, ProcedureDescriptor> procedures, Action<ProcedureFault> onProcedureFault,
        IReadOnlyDictionary<(string Method, string Route), RestCommandHandler> restCommands,
        IReadOnlyDictionary<Type, IChildDatabaseRegistry> childRegistrations, Func<uint, bool> isAppVersionInvalid,
        uint serverVersion, ChainLog? chainLog, IDisposable? collector) {
        this.database = database;
        this.tableCompatAdapters = tableCompatAdapters;
        this.procedures = procedures;
        this.onProcedureFault = onProcedureFault;
        this.restCommands = restCommands;
        this.childRegistrations = childRegistrations;
        this.isAppVersionInvalid = isAppVersionInvalid;
        this.serverVersion = serverVersion;
        this.chainLog = chainLog;
        this.collector = collector;
    }

    public TDb GetDatabase<TDb>() where TDb : notnull => (TDb)database;

    public bool IsAppVersionInvalid(uint version) => isAppVersionInvalid(version);

    public uint ServerVersion => serverVersion;

    // The process's durable key/value store, kept in the Root database's cold storage (docs/manual/prefs.md).
    public RhinoPrefs Prefs => ((IHostedDatabase)database).Cold?.Prefs
        ?? throw new InvalidOperationException("Prefs need the Root database's cold storage - this Root was created without one.");

    internal ChainLog? ChainLog => chainLog;

    internal string ChildParticipantId<TChildDb>(object key) =>
        childRegistrations.TryGetValue(typeof(TChildDb), out var registry)
            ? registry.ParticipantIdFor(key)
            : throw new InvalidOperationException($"No child database of type '{typeof(TChildDb).Name}' was registered via AddChildDatabase.");

    public TableCompatOptions<TRow>? GetTableCompatAdapter<TRow>() =>
        tableCompatAdapters.TryGetValue(typeof(TRow), out var options)
            ? (TableCompatOptions<TRow>)options
            : null;

    public IReadOnlyCollection<ProcedureDescriptor> Procedures => (IReadOnlyCollection<ProcedureDescriptor>)procedures.Values;

    public async Task<Result<ReadOnlyMemory<byte>>> DispatchProcedureAsync(uint hash, Session session, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (!procedures.TryGetValue(hash, out var procedure))
            return Result<ReadOnlyMemory<byte>>.Error(DbError.UnknownProcedure());

        Result<ReadOnlyMemory<byte>> result;
        try {
            result = await procedure.Handler(this, session, body, ct);
        } catch (Exception ex) {
            result = Result<ReadOnlyMemory<byte>>.Error(DbError.ProcedureFailed(ex));
        }

        if (result.IsError() && result.GetError() is { Kind: ErrorKind.ProcedureFailed or ErrorKind.ProcedureArgsInvalid } fault)
            ReportFault(procedure, fault, session);
        return result;
    }

    private void ReportFault(ProcedureDescriptor procedure, DbError fault, Session session) {
        try {
            var exception = fault.ToException();
            onProcedureFault(new ProcedureFault(procedure.Name, procedure.Hash, fault.Kind, exception.InnerException ?? exception, session));
        } catch { /* a logging hook must never turn a request fault into a crash */ }
    }

    public IEnumerable<(string Method, string Route)> RegisteredRestCommands => restCommands.Keys;

    public async Task<RestResponse> DispatchRestAsync(string method, string route, RestRequest request, CancellationToken ct) {
        if (!restCommands.TryGetValue((method, route), out var handler)) return new RestResponse(404, ReadOnlyMemory<byte>.Empty);
        return await handler(this, request, ct);
    }

    public Task<Result<TChildDb>> GetOrActivateChildAsync<TChildDb, TTx, TKey>(TKey key, CancellationToken ct = default)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
        if (!childRegistrations.TryGetValue(typeof(TChildDb), out var registry))
            return Task.FromResult(Result<TChildDb>.Error(DbError.SystemFailure(new InvalidOperationException(
                $"No child database of type '{typeof(TChildDb).Name}' was registered via AddChildDatabase."))));
        return ((ChildDatabaseRegistry<TChildDb, TTx, TKey>)registry).GetOrActivateAsync(key, ct);
    }

    public Task<Result> DisposeChildAsync<TChildDb, TTx, TKey>(TKey key, CancellationToken drainCt = default)
        where TChildDb : DbContext<TTx> where TTx : ITransaction where TKey : notnull {
        if (!childRegistrations.TryGetValue(typeof(TChildDb), out var registry))
            return Task.FromResult(Result.Error(DbError.SystemFailure(new InvalidOperationException(
                $"No child database of type '{typeof(TChildDb).Name}' was registered via AddChildDatabase."))));
        return ((ChildDatabaseRegistry<TChildDb, TTx, TKey>)registry).DisposeAsync(key, drainCt);
    }

    public Task<Result> DispatchClientConnectAsync(Session session) => ((IRhinoClientLifecycle)database).OnClientConnectAsync(session);

    public Task<Result> DispatchClientDisconnectAsync(Session session) => ((IRhinoClientLifecycle)database).OnClientDisconnectAsync(session);

    public int ArchiveCollectorCount => collector is null ? 0 : 1;

    public int? NetworkPort => networkHost?.Port;

    internal void AttachNetworkHost(IRhinoNetworkHost host) => networkHost = host;

    internal async Task ShutdownForUnrecoverableErrorAsync() {
        if (networkHost is { } network) {
            try { await network.DisposeAsync(); } catch { /* best effort */ }
        }

        var colds = new List<ColdStore>();
        if (((IHostedDatabase)database).Cold is { } rootCold) colds.Add(rootCold);
        foreach (var registry in childRegistrations.Values) colds.AddRange(registry.ActiveColdStores());

        var flushes = new List<Task>();
        foreach (var cold in colds) {
            try { flushes.Add(cold.Wal.FlushAsync()); } catch { /* best effort */ }
        }
        try { await Task.WhenAll(flushes); } catch { /* best effort */ }
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        if (networkHost is not null) {
            try { networkHost.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* best-effort - shutdown must not throw */ }
        }
        foreach (var registry in childRegistrations.Values) registry.CloseAllBestEffort();
        if (collector is not null) {
            try { collector.Dispose(); } catch { /* best-effort - shutdown must not throw */ }
        }
    }
}
