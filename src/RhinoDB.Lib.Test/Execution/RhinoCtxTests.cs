using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Hosting.Test;
using RhinoDB.Lib.Realtime;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Execution.Test;

public class RhinoCtxTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-rhinoctx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class FakeDb(ColdStore cold) : DbContext(cold);

    [Test]
    public async Task ServerVersion_OnAHostBackedCtx_ReturnsTheHostsConfiguredPackedVersion() {
        RhinoHostConfigTestHelper.WriteConfig(dir, new HostConfig { ColdPath = dir }, new ServerConfig { Version = "1.2.3" });
        var hostResult = await RhinoHostBuilder.Create(dir)
            .AddDatabase<FakeDb, DefaultTransaction>(options => options.CreateDb = cold => new FakeDb(cold))
            .BuildAsync();
        var host = hostResult.Unwrap();

        var ctx = new RhinoCtx(host, Identity.Anonymous);

        Assert.That(ctx.ServerVersion, Is.EqualTo(ServerVersionParser.Parse("1.2.3")));
        host.GetDatabase<FakeDb>().Cold!.Dispose();
        host.Dispose();
    }

    [Test]
    public void ServerVersion_OnADirectDbBackedCtx_Throws() {
        // Every lifecycle hook gets a direct-db-backed RhinoCtx (fires before/independent of any
        // RhinoHost), so server version - a host-level concern - is structurally unavailable there,
        // same as Child database access.
        var ctx = new RhinoCtx(new DbContext(), Identity.System);

        var ex = Assert.Throws<InvalidOperationException>(() => _ = ctx.ServerVersion);
        Assert.That(ex!.Message, Does.Contain("Server version needs a RhinoHost-backed RhinoCtx"));
    }
}
