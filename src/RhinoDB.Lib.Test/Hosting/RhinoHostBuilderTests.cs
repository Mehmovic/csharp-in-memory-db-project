using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Durability;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Hosting.Test;

// RhinoHostBuilder is the thin, generic wiring layer a scaffolded Program.cs calls into - real
// file I/O against real temp directories (matching this project's established convention for
// ColdStore-touching tests), but TDb is a plain DbContext-derived test double since the builder is
// deliberately delegate-driven rather than coupled to a generated {Db}/{Db}Loader type. A process
// hosts exactly one database now (AddDatabase may only be called once) - settings come from a real
// rdbsettings.json written per test via RhinoHostConfigTestHelper, never command-line flags.
public class RhinoHostBuilderTests {
    private string dirA = "";
    private string dirB = "";

    [SetUp]
    public void SetUp() {
        dirA = Path.Combine(Path.GetTempPath(), "rhinodb-rhinohostbuilder-tests", Guid.NewGuid().ToString("N"), "a");
        dirB = Path.Combine(Path.GetTempPath(), "rhinodb-rhinohostbuilder-tests", Guid.NewGuid().ToString("N"), "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(Path.GetDirectoryName(dirA)!, recursive: true); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(Path.GetDirectoryName(dirB)!, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class FakeDb(ColdStore cold) : DbContext(cold);

    static private RhinoHostBuilder CreateBuilder(string dir, HostConfig? host = null) {
        RhinoHostConfigTestHelper.WriteConfig(dir, host ?? new HostConfig { ColdPath = dir });
        return RhinoHostBuilder.Create(dir);
    }

    [Test]
    public async Task BuildAsync_InRunMode_CallsLoadAsync_NotLoadFromGenesis() {
        var loadAsyncCalled = false;
        var loadFromGenesisCalled = false;

        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.LoadFromGenesis = (_, _, _) => { loadFromGenesisCalled = true; return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        Assert.That(loadAsyncCalled, Is.True);
        Assert.That(loadFromGenesisCalled, Is.False);
        hostResult.Unwrap().GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InRunMode_DoesNotRequireLoadFromGenesisToBeConfigured() {
        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
                // LoadFromGenesis deliberately left unconfigured.
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True, "Run mode never calls LoadFromGenesis, so it must not be required.");
        hostResult.Unwrap().GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InReplayMode_CallsLoadFromGenesisWithTheParsedUpToLsn_NotLoadAsync() {
        var loadAsyncCalled = false;
        ulong? seenUpToLsn = null;

        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "replay", ReplayUpToLsn = 7 })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.LoadFromGenesis = (_, _, upToLsn) => { seenUpToLsn = upToLsn; return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        Assert.That(loadAsyncCalled, Is.False);
        Assert.That(seenUpToLsn, Is.EqualTo(7L));
        hostResult.Unwrap().GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InReplayMode_DoesNotRequireLoadAsyncToBeConfigured() {
        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "replay" })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadFromGenesis = (_, _, _) => Result.Ok();
                // LoadAsync deliberately left unconfigured.
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True, "Replay mode never calls LoadAsync, so it must not be required.");
        hostResult.Unwrap().GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InMigrateModeWithoutRunMigrationConfigured_FailsWithoutCallingEitherLoader() {
        var loadAsyncCalled = false;
        var loadFromGenesisCalled = false;

        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "migrate" })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.LoadFromGenesis = (_, _, _) => { loadFromGenesisCalled = true; return Result.Ok(); };
                // RunMigration deliberately left unconfigured - Migrate mode requires it.
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True, "RhinoRunMode.Migrate needs RunMigration configured - fail loudly, not silently no-op.");
        Assert.That(loadAsyncCalled, Is.False);
        Assert.That(loadFromGenesisCalled, Is.False);
    }

    [Test]
    public async Task BuildAsync_InMigrateMode_CallsRunMigration_NotLoadAsyncOrLoadFromGenesis() {
        var runMigrationCalled = false;
        var loadAsyncCalled = false;
        var loadFromGenesisCalled = false;

        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "migrate" })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.LoadFromGenesis = (_, _, _) => { loadFromGenesisCalled = true; return Result.Ok(); };
                options.RunMigration = _ => { runMigrationCalled = true; return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        Assert.That(runMigrationCalled, Is.True);
        Assert.That(loadAsyncCalled, Is.False);
        Assert.That(loadFromGenesisCalled, Is.False);
        hostResult.Unwrap().GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InMigrateMode_WhenRunMigrationItselfFails_PropagatesTheError() {
        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "migrate" })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.RunMigration = _ => Result.Error(DbError.SystemFailure(new InvalidOperationException("boom")));
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True);
    }

    // ---- Self-healing generation check (RhinoRunMode.Run) ----

    [Test]
    public async Task BuildAsync_InRunMode_WhenCurrentGenerationMatchesBinary_DoesNotCallRunMigration() {
        var runMigrationCalled = false;
        var loadAsyncCalled = false;

        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.GBinary = 0;
                options.IsGenerationInvalid = _ => false;
                options.RunMigration = _ => { runMigrationCalled = true; return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        Assert.That(runMigrationCalled, Is.False, "G_db (0, a fresh database) already matches G_binary - no migration needed.");
        Assert.That(loadAsyncCalled, Is.True);
        hostResult.Unwrap().GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InRunMode_WhenBinaryIsNewerThanCurrentGeneration_CallsRunMigrationBeforeLoadAsync() {
        var callOrder = new List<string>();

        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { callOrder.Add("LoadAsync"); return Task.CompletedTask; };
                options.GBinary = 1;
                options.IsGenerationInvalid = _ => false;
                options.RunMigration = _ => { callOrder.Add("RunMigration"); return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        Assert.That(callOrder, Is.EqualTo(new[] { "RunMigration", "LoadAsync" }), "self-healing: migrate first, then load, in one Run-mode bring-up.");
        hostResult.Unwrap().GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InRunMode_WhenTheCurrentGenerationIsDeclaredInvalid_RefusesWithoutCallingRunMigrationOrLoadAsync() {
        var runMigrationCalled = false;
        var loadAsyncCalled = false;

        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.GBinary = 0;
                options.IsGenerationInvalid = _ => true;
                options.RunMigration = _ => { runMigrationCalled = true; return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True, "point C's invalid-generation check must refuse, ahead of everything else.");
        Assert.That(runMigrationCalled, Is.False);
        Assert.That(loadAsyncCalled, Is.False);
    }

    [Test]
    public async Task BuildAsync_InRunMode_WhenBinaryIsOlderThanCurrentGeneration_RefusesAsADowngrade() {
        // Bump the database to generation 5 first (closed before the real builder-driven Open below) - the
        // exact scenario an accidental binary rollback (deploying an OLDER build against
        // already-migrated data) produces.
        using (var seedCold = ColdStore.Open(dirA).Unwrap())
            Assert.That(seedCold.RunMigration(5, []).IsOk(), Is.True);

        var runMigrationCalled = false;
        var loadAsyncCalled = false;

        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.GBinary = 3;
                options.IsGenerationInvalid = _ => false;
                options.RunMigration = _ => { runMigrationCalled = true; return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True, "the database is AHEAD of this binary - refuse, never silently downgrade.");
        Assert.That(runMigrationCalled, Is.False);
        Assert.That(loadAsyncCalled, Is.False);
    }

    [Test]
    public async Task BuildAsync_WithoutCreateDbConfigured_FailsRegardlessOfMode() {
        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.LoadAsync = _ => Task.CompletedTask;
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True);
    }

    [Test]
    public async Task BuildAsync_WithoutAddDatabaseEverCalled_Fails() {
        var hostResult = await CreateBuilder(dirA).BuildAsync();

        Assert.That(hostResult.IsError(), Is.True);
    }

    // ---- one process, one database ----

    [Test]
    public void AddDatabase_CalledTwice_Throws() {
        var builder = CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => { options.CreateDb = cold => new FakeDb(cold); });

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddDatabase<FakeDb, DefaultTransaction>(options => { options.CreateDb = cold => new FakeDb(cold); }));
    }

    [Test]
    public async Task GetDatabase_WithTheWrongType_ThrowsInvalidCastException() {
        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
            })
            .BuildAsync();

        var host = hostResult.Unwrap();
        Assert.Throws<InvalidCastException>(() => host.GetDatabase<string>());
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    // ---- RhinoRunMode.WalPrune ----

    [Test]
    public async Task BuildAsync_InPruneModeWithoutTargetGenerationConfigured_Fails() {
        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "wal-prune" })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True, "Prune mode needs Host.WalKeepGenerations - fail loudly, not silently no-op.");
    }

    [Test]
    public async Task BuildAsync_InPruneMode_DeletesSegmentsOlderThanTheTargetAndAdvancesTheFloor() {
        var archiveDir = Path.Combine(dirA, WalArchive.ArchiveDirectoryName);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [new DecodedWalEntry(1, WalEntryKind.Operation, [new WalChange(1, ChangeKind.Insert, [1], [9])])], generation: 0);
        WalArchive.WriteSegment(archiveDir, Guid.NewGuid(), [new DecodedWalEntry(2, WalEntryKind.Operation, [new WalChange(1, ChangeKind.Insert, [2], [9])])], generation: 2);

        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "wal-prune", WalKeepGenerations = 2 })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        var db = hostResult.Unwrap().GetDatabase<FakeDb>();
        Assert.That(db.Cold!.ReadRetainedFromGeneration().Unwrap(), Is.EqualTo(2));
        Assert.That(WalArchive.ReadOldestRetainedGeneration(dirA).Unwrap().Get(), Is.EqualTo(2),
            "generation 0's segment must be gone; generation 2's must survive untouched.");
        db.Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InPruneMode_WhenTargetIsBehindTheCurrentFloor_Refuses() {
        using (var seedCold = ColdStore.Open(dirA).Unwrap())
            Assert.That(seedCold.WriteRetainedFromGeneration(5).IsOk(), Is.True);

        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "wal-prune", WalKeepGenerations = 3 })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True, "the floor only ever moves forward - refuse a backward request.");
    }

    // ---- RhinoRunMode.WalMigrate ----

    [Test]
    public async Task BuildAsync_InMigrateWalArchiveModeWithoutItConfigured_Fails() {
        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "wal-migrate" })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True, "MigrateWalArchive mode needs the delegate configured - fail loudly, not silently no-op.");
    }

    [Test]
    public async Task BuildAsync_InMigrateWalArchiveMode_CallsMigrateWalArchive_NotAnyOtherDelegate() {
        var migrateWalArchiveCalled = false;
        var runMigrationCalled = false;
        var loadAsyncCalled = false;

        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "wal-migrate" })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.RunMigration = _ => { runMigrationCalled = true; return Result.Ok(); };
                options.MigrateWalArchive = _ => { migrateWalArchiveCalled = true; return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        Assert.That(migrateWalArchiveCalled, Is.True);
        Assert.That(runMigrationCalled, Is.False);
        Assert.That(loadAsyncCalled, Is.False);
        hostResult.Unwrap().GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InMigrateWalArchiveMode_WhenItFails_PropagatesTheError() {
        var hostResult = await CreateBuilder(dirA, new HostConfig { ColdPath = dirA, Mode = "wal-migrate" })
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.MigrateWalArchive = _ => Result.Error(DbError.SystemFailure(new InvalidOperationException("boom")));
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True);
    }

    // ---- the archive collector allocates NOTHING when retention is off ----

    [Test]
    public async Task BuildAsync_WithRetentionOff_CreatesNoCollectorAndNoTimer() {
        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        using var host = hostResult.Unwrap();
        Assert.That(host.ArchiveCollectorCount, Is.EqualTo(0),
            "a disabled feature should not leave a live object behind - the collector holds a Timer.");
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_WithRetentionOn_CreatesExactlyOneCollector() {
        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
                options.ArchiveRetention = new ArchiveRetentionPolicy(TimeSpan.FromHours(4), TimeSpan.FromDays(30));
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        using var host = hostResult.Unwrap();
        Assert.That(host.ArchiveCollectorCount, Is.EqualTo(1));
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task RhinoHost_Dispose_WithACollectorRunning_IsSafeToCallTwice() {
        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
                options.ArchiveRetention = new ArchiveRetentionPolicy(TimeSpan.FromHours(4), TimeSpan.FromDays(30));
            })
            .BuildAsync();

        var host = hostResult.Unwrap();
        Assert.DoesNotThrow(host.Dispose);
        Assert.DoesNotThrow(host.Dispose, "Dispose must be idempotent - a host may be torn down twice.");
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    // ---- no RhinoDB.Lib.Server reference = no network host, even though Run mode defaults http-enabled to true ----

    [Test]
    public async Task BuildAsync_InRunMode_WithoutRhinoDbLibServerReferenced_StartsNoNetworkHost() {
        // This project (RhinoDB.Lib.Test) deliberately does not reference RhinoDB.Lib.Server, so its
        // [ModuleInitializer] never runs and RhinoNetworkHostProvider.Factory stays null - proving
        // "referencing the package is what turns the default server on," not Run mode alone.
        var hostResult = await CreateBuilder(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        using var host = hostResult.Unwrap();
        Assert.That(host.NetworkPort, Is.Null);
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }
}
