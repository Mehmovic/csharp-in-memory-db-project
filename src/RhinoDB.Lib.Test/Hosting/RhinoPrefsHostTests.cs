using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Realtime;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Hosting.Test;

// One RhinoPrefs per process: the Root's, reached the same way from the host, a request's context and a lifecycle hook.
public class RhinoPrefsHostTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-prefs-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class RootDb(ColdStore cold) : DbContext(cold) {
        public long SeasonSeenAtStart = -1;

        protected internal override Task<Result> OnStartAsync() {
            SeasonSeenAtStart = new RhinoCtx(this, Identity.System).Prefs.GetInt64("season", 0).Unwrap();
            return base.OnStartAsync();
        }
    }

    private async Task<RhinoHost> Build(string? cacheDuration = null) {
        File.WriteAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName), $$"""
            { "Host": { "ColdPath": {{System.Text.Json.JsonSerializer.Serialize(dir)}}, "HttpEnabled": false, "HttpPort": 0 },
              "Prefs": { "CacheDuration": "{{cacheDuration ?? "00:30:00"}}" } }
            """);
        return (await RhinoHostBuilder.Create(dir)
            .AddDatabase<RootDb, DefaultTransaction>(o => o.CreateDb = cold => new RootDb(cold))
            .BuildAsync()).Unwrap();
    }

    static private void Shutdown(RhinoHost host) {
        var cold = host.GetDatabase<RootDb>().Cold;
        host.Dispose();
        cold?.Dispose();
    }

    [Test]
    public async Task TheHost_ARequestContext_AndAHookContext_ShareOnePrefs() {
        var host = await Build();

        var fromRequest = new RhinoCtx(host, Identity.Anonymous).Prefs;
        var fromHook = new RhinoCtx(host.GetDatabase<RootDb>(), Identity.System).Prefs;

        Assert.That(fromRequest, Is.SameAs(host.Prefs));
        Assert.That(fromHook, Is.SameAs(host.Prefs));
        Shutdown(host);
    }

    [Test]
    public async Task PrefsSurviveARestart_AndAreReadableFromOnStart() {
        var host = await Build();
        Assert.That((await host.Prefs.SetInt64Async("season", 7)).IsOk(), Is.True);
        Shutdown(host);

        var restarted = await Build();

        Assert.That(restarted.Prefs.GetInt64("season").Unwrap(), Is.EqualTo(7));
        Assert.That(restarted.GetDatabase<RootDb>().SeasonSeenAtStart, Is.EqualTo(7), "a hook runs before the host exists and still reaches them.");
        Shutdown(restarted);
    }

    [Test]
    public async Task PrefsCacheDuration_FromRdbsettings_IsTheDefault() {
        var host = await Build(cacheDuration: "00:05:00");

        Assert.That(host.Prefs.DefaultCacheDuration, Is.EqualTo(TimeSpan.FromMinutes(5)));
        Shutdown(host);
    }

    [Test]
    public async Task AnInvalidPrefsCacheDuration_FailsTheBuild_WithAnError() {
        File.WriteAllText(Path.Combine(dir, GeneratorConfigLoader.ConfigFileName), $$"""
            { "Host": { "ColdPath": {{System.Text.Json.JsonSerializer.Serialize(dir)}}, "HttpEnabled": false }, "Prefs": { "CacheDuration": "soon" } }
            """);

        var built = await RhinoHostBuilder.Create(dir)
            .AddDatabase<RootDb, DefaultTransaction>(o => o.CreateDb = cold => new RootDb(cold))
            .BuildAsync();

        Assert.That(built.IsError(), Is.True);
        Assert.That(built.GetError().ToException().Message, Does.Contain("Prefs.CacheDuration"));
    }

    [Test]
    public void ADatabaseWithoutColdStorage_HasNoPrefs() {
        var ctx = new RhinoCtx(new DbContext(), Identity.System);

        Assert.Throws<InvalidOperationException>(() => _ = ctx.Prefs);
    }
}
