using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Hosting.Test;

// Part H: each child database is architecturally nothing more than another DbContext-derived
// instance, created dynamically (keyed by a runtime TKey) instead of statically at startup - these
// tests exercise that through the real RhinoHostBuilder/RhinoHost surface, real ColdStore directories
// under a real root ColdPath, not mocks.
public class ChildDatabaseTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-childdatabase-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class RootDb(ColdStore cold) : DbContext(cold);

    private sealed class ChildDb(ColdStore cold) : DbContext(cold) {
        public bool InitCalled;
        public bool StartCalled;
        protected internal override Task<Result> OnInitAsync() { InitCalled = true; return base.OnInitAsync(); }
        protected internal override Task<Result> OnStartAsync() { StartCalled = true; return base.OnStartAsync(); }
    }

    static private RhinoHostBuilder CreateBuilder(string directory) {
        RhinoHostConfigTestHelper.WriteConfig(directory, new HostConfig { ColdPath = directory });
        return RhinoHostBuilder.Create(directory);
    }

    private async Task<RhinoHost> BuildHostAsync(RhinoHostBuilder builder) {
        var hostResult = await builder.AddDatabase<RootDb, DefaultTransaction>(options => options.CreateDb = cold => new RootDb(cold)).BuildAsync();
        return hostResult.Unwrap();
    }

    [Test]
    public async Task GetOrActivateChildAsync_WithoutAddChildDatabase_ReturnsAnError() {
        using var host = await BuildHostAsync(CreateBuilder(dir));

        var result = await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1");

        Assert.That(result.IsError(), Is.True);
        host.GetDatabase<RootDb>().Cold!.Dispose();
    }

    [Test]
    public async Task GetOrActivateChildAsync_FirstCall_ActivatesAndRunsOnInitAndOnStart() {
        var builder = CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(options => options.CreateDb = cold => new ChildDb(cold));
        using var host = await BuildHostAsync(builder);

        var result = await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1");

        Assert.That(result.IsOk(), Is.True);
        Assert.That(result.Unwrap().InitCalled, Is.True);
        Assert.That(result.Unwrap().StartCalled, Is.True);
        host.GetDatabase<RootDb>().Cold!.Dispose();
        result.Unwrap().Cold!.Dispose();
    }

    [Test]
    public async Task GetOrActivateChildAsync_CalledTwiceWithTheSameKey_ReturnsTheSameCachedInstance_AndDoesNotRerunOnInit() {
        var builder = CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(options => options.CreateDb = cold => new ChildDb(cold));
        using var host = await BuildHostAsync(builder);

        var first = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1")).Unwrap();
        first.InitCalled = false; // reset to prove a second GetOrActivate does not call it again
        var second = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1")).Unwrap();

        Assert.That(second, Is.SameAs(first));
        Assert.That(second.InitCalled, Is.False);
        host.GetDatabase<RootDb>().Cold!.Dispose();
        first.Cold!.Dispose();
    }

    [Test]
    public async Task GetOrActivateChildAsync_DifferentKeys_GetIndependentInstances() {
        var builder = CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(options => options.CreateDb = cold => new ChildDb(cold));
        using var host = await BuildHostAsync(builder);

        var a = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-a")).Unwrap();
        var b = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-b")).Unwrap();

        Assert.That(a, Is.Not.SameAs(b));
        host.GetDatabase<RootDb>().Cold!.Dispose();
        a.Cold!.Dispose();
        b.Cold!.Dispose();
    }

    [Test]
    public async Task ActivatedChild_CanRunRealOperations() {
        var builder = CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(options => options.CreateDb = cold => new ChildDb(cold));
        using var host = await BuildHostAsync(builder);
        var child = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1")).Unwrap();

        var ran = false;
        var runResult = await child.Run(_ => { ran = true; return Result.Ok(); });

        Assert.That(runResult.IsOk(), Is.True);
        Assert.That(ran, Is.True);
        host.GetDatabase<RootDb>().Cold!.Dispose();
        child.Cold!.Dispose();
    }

    [Test]
    public async Task DisposeChildAsync_NeverActivatedKey_IsANoOp() {
        var builder = CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(options => options.CreateDb = cold => new ChildDb(cold));
        using var host = await BuildHostAsync(builder);

        var result = await host.DisposeChildAsync<ChildDb, DefaultTransaction, string>("never-activated");

        Assert.That(result.IsOk(), Is.True);
        host.GetDatabase<RootDb>().Cold!.Dispose();
    }

    [Test]
    public async Task DisposeChildAsync_DeletesTheChildsDirectory_AndReactivatingTheSameKeyStartsFresh() {
        var builder = CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(options => options.CreateDb = cold => new ChildDb(cold));
        using var host = await BuildHostAsync(builder);

        var first = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1")).Unwrap();
        var childDir = Path.Combine(dir, "Children", nameof(ChildDb), "session-1");
        Assert.That(Directory.Exists(childDir), Is.True, "precondition: activation must have created the directory.");

        var disposeResult = await host.DisposeChildAsync<ChildDb, DefaultTransaction, string>("session-1");

        Assert.That(disposeResult.IsOk(), Is.True);
        Assert.That(Directory.Exists(childDir), Is.False, "DisposeChildAsync must delete the child's directory, not just close it.");

        var second = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1")).Unwrap();
        Assert.That(second.InitCalled, Is.True, "a key whose directory was deleted must be treated as fresh again on reactivation.");

        host.GetDatabase<RootDb>().Cold!.Dispose();
        second.Cold!.Dispose();
    }

    [Test]
    public async Task DisposeChildAsync_ForAChildNotActivatedSinceARestart_StillDeletesItsDirectory_WithoutActivatingIt() {
        // Children are lazy: after a restart a child's directory is back on disk long before anything activates it.
        var childDir = Path.Combine(dir, "Children", nameof(ChildDb), "session-1");
        var first = await BuildHostAsync(CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(o => o.CreateDb = cold => new ChildDb(cold)));
        (await first.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1")).Unwrap();
        first.GetDatabase<RootDb>().Cold!.Dispose();
        first.Dispose();
        Assert.That(Directory.Exists(childDir), Is.True, "precondition: the child's data survives the restart.");

        var created = 0;
        using var restarted = await BuildHostAsync(CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(o => o.CreateDb = cold => {
            created++;
            return new ChildDb(cold);
        }));

        var result = await restarted.DisposeChildAsync<ChildDb, DefaultTransaction, string>("session-1");

        Assert.That(result.IsOk(), Is.True);
        Assert.That(Directory.Exists(childDir), Is.False, "an inactive child must still be deleted - not silently reported as disposed.");
        Assert.That(created, Is.EqualTo(0), "it's deleted straight from disk - never activated (no recovery, OnInit or OnStart) just to be thrown away.");
        restarted.GetDatabase<RootDb>().Cold!.Dispose();
    }

    [Test]
    public async Task GetOrActivateChildAsync_ConcurrentCallsForOneKey_ActivateItExactlyOnce() {
        var created = 0;
        var builder = CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(o => o.CreateDb = cold => {
            Interlocked.Increment(ref created);
            return new ChildDb(cold);
        });
        using var host = await BuildHostAsync(builder);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1")));

        Assert.That(results.All(r => r.IsOk()), Is.True);
        Assert.That(results.Select(r => r.Unwrap()).Distinct().Count(), Is.EqualTo(1));
        Assert.That(created, Is.EqualTo(1), "two activations must never both open the same directory.");
        host.GetDatabase<RootDb>().Cold!.Dispose();
        results[0].Unwrap().Cold!.Dispose();
    }

    [Test]
    public async Task DisposedChild_RejectsFurtherWorkOnTheStaleReference() {
        var builder = CreateBuilder(dir).AddChildDatabase<ChildDb, DefaultTransaction, string>(options => options.CreateDb = cold => new ChildDb(cold));
        using var host = await BuildHostAsync(builder);
        var child = (await host.GetOrActivateChildAsync<ChildDb, DefaultTransaction, string>("session-1")).Unwrap();

        await host.DisposeChildAsync<ChildDb, DefaultTransaction, string>("session-1");
        var runResult = await child.Run(_ => Result.Ok());

        Assert.That(runResult.IsError(), Is.True);
        Assert.That(runResult.GetError().Kind, Is.EqualTo(ErrorKind.DatabaseClosing));
        host.GetDatabase<RootDb>().Cold!.Dispose();
    }

    [Test]
    public void AddChildDatabase_CalledTwiceForTheSameChildType_Throws() {
        Assert.Throws<ArgumentException>(() =>
            CreateBuilder(dir)
                .AddChildDatabase<ChildDb, DefaultTransaction, string>(options => options.CreateDb = cold => new ChildDb(cold))
                .AddChildDatabase<ChildDb, DefaultTransaction, string>(options => options.CreateDb = cold => new ChildDb(cold)));
    }
}
