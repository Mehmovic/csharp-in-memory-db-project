using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;

namespace RhinoDB.Lib.Hosting.Test;

// RhinoHostBuilder is the thin, generic wiring layer a scaffolded Program.cs calls into - real
// file I/O against real temp directories (matching this project's established convention for
// ColdStore-touching tests), but TDb is a plain DbContext-derived test double since the builder is
// deliberately delegate-driven rather than coupled to a generated {Db}/{Db}Loader type. Each
// registered database gets its own flag namespace (RhinoHostOptions.Parse's prefix) so one shared
// args[] can configure several actor-model databases in one process - that's the scenario these
// tests exist to prove, alongside fail-fast build and mode-conditional delegate requirements.
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

    [Test]
    public async Task BuildAsync_InRunMode_CallsLoadAsync_NotLoadFromGenesis() {
        var loadAsyncCalled = false;
        var loadFromGenesisCalled = false;

        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.LoadFromGenesis = (_, _, _) => { loadFromGenesisCalled = true; return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        Assert.That(loadAsyncCalled, Is.True);
        Assert.That(loadFromGenesisCalled, Is.False);
        hostResult.Unwrap().GetDatabase<FakeDb>("game").Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InRunMode_DoesNotRequireLoadFromGenesisToBeConfigured() {
        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
                // LoadFromGenesis deliberately left unconfigured.
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True, "Run mode never calls LoadFromGenesis, so it must not be required.");
        hostResult.Unwrap().GetDatabase<FakeDb>("game").Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InReplayMode_CallsLoadFromGenesisWithTheParsedUpToLsn_NotLoadAsync() {
        var loadAsyncCalled = false;
        long? seenUpToLsn = null;

        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}", "--game.mode=replay", "--game.replay-upto-lsn=7"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { loadAsyncCalled = true; return Task.CompletedTask; };
                options.LoadFromGenesis = (_, _, upToLsn) => { seenUpToLsn = upToLsn; return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        Assert.That(loadAsyncCalled, Is.False);
        Assert.That(seenUpToLsn, Is.EqualTo(7L));
        hostResult.Unwrap().GetDatabase<FakeDb>("game").Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InReplayMode_DoesNotRequireLoadAsyncToBeConfigured() {
        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}", "--game.mode=replay"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadFromGenesis = (_, _, _) => Result.Ok();
                // LoadAsync deliberately left unconfigured.
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True, "Replay mode never calls LoadAsync, so it must not be required.");
        hostResult.Unwrap().GetDatabase<FakeDb>("game").Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InMigrateModeWithoutRunMigrationConfigured_FailsWithoutCallingEitherLoader() {
        var loadAsyncCalled = false;
        var loadFromGenesisCalled = false;

        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}", "--game.mode=migrate"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
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

        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}", "--game.mode=migrate"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
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
        hostResult.Unwrap().GetDatabase<FakeDb>("game").Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InMigrateMode_WhenRunMigrationItselfFails_PropagatesTheError() {
        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}", "--game.mode=migrate"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
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

        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
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
        hostResult.Unwrap().GetDatabase<FakeDb>("game").Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InRunMode_WhenBinaryIsNewerThanCurrentGeneration_CallsRunMigrationBeforeLoadAsync() {
        var callOrder = new List<string>();

        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => { callOrder.Add("LoadAsync"); return Task.CompletedTask; };
                options.GBinary = 1;
                options.IsGenerationInvalid = _ => false;
                options.RunMigration = _ => { callOrder.Add("RunMigration"); return Result.Ok(); };
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        Assert.That(callOrder, Is.EqualTo(new[] { "RunMigration", "LoadAsync" }), "self-healing: migrate first, then load, in one Run-mode bring-up.");
        hostResult.Unwrap().GetDatabase<FakeDb>("game").Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_InRunMode_WhenTheCurrentGenerationIsDeclaredInvalid_RefusesWithoutCallingRunMigrationOrLoadAsync() {
        var runMigrationCalled = false;
        var loadAsyncCalled = false;

        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
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

        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
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
        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
                options.LoadAsync = _ => Task.CompletedTask;
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True);
    }

    [Test]
    public async Task BuildAsync_WithTwoDatabases_ParsesEachOnesFlagsFromTheSharedArgsIndependently() {
        var hostResult = await RhinoHostBuilder.Create([
                $"--players.cold-path={dirA}", "--players.mode=run",
                $"--matches.cold-path={dirB}", "--matches.mode=replay", "--matches.replay-upto-lsn=3",
            ])
            .AddDatabase<FakeDb, DefaultTransaction>("players", options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
            })
            .AddDatabase<FakeDb, DefaultTransaction>("matches", options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadFromGenesis = (_, _, _) => Result.Ok();
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        var host = hostResult.Unwrap();
        Assert.That(host.GetDatabase<FakeDb>("players"), Is.Not.SameAs(host.GetDatabase<FakeDb>("matches")));
        host.GetDatabase<FakeDb>("players").Cold!.Dispose();
        host.GetDatabase<FakeDb>("matches").Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_WhenASecondDatabaseFailsToConfigure_FailsTheWholeBuild() {
        var hostResult = await RhinoHostBuilder.Create([$"--first.cold-path={dirA}", $"--second.cold-path={dirB}"])
            .AddDatabase<FakeDb, DefaultTransaction>("first", options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
            })
            .AddDatabase<FakeDb, DefaultTransaction>("second", options => {
                // CreateDb deliberately left unconfigured - this registration must fail.
                options.LoadAsync = _ => Task.CompletedTask;
            })
            .BuildAsync();

        Assert.That(hostResult.IsError(), Is.True, "One broken database must fail the whole build, not come up partially.");
    }

    [Test]
    public void AddDatabase_WithADuplicateName_Throws() {
        var builder = RhinoHostBuilder.Create([$"--game.cold-path={dirA}"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => { options.CreateDb = cold => new FakeDb(cold); });

        Assert.Throws<ArgumentException>(() =>
            builder.AddDatabase<FakeDb, DefaultTransaction>("game", options => { options.CreateDb = cold => new FakeDb(cold); }));
    }

    [Test]
    public async Task GetDatabase_WithAnUnregisteredName_Throws() {
        var hostResult = await RhinoHostBuilder.Create([$"--game.cold-path={dirA}"])
            .AddDatabase<FakeDb, DefaultTransaction>("game", options => {
                options.CreateDb = cold => new FakeDb(cold);
                options.LoadAsync = _ => Task.CompletedTask;
            })
            .BuildAsync();

        var host = hostResult.Unwrap();
        Assert.Throws<KeyNotFoundException>(() => host.GetDatabase<FakeDb>("nonexistent"));
        host.GetDatabase<FakeDb>("game").Cold!.Dispose();
    }
}
