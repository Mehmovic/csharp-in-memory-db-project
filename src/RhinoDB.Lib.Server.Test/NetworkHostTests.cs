using System.Net.Http;
using System.Text.Json;

using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Server.Test;

// Real Kestrel, real HttpClient/TCP round-trips - referencing RhinoDB.Lib.Server (unlike
// RhinoDB.Lib.Test) is what makes RhinoNetworkHostProvider.Factory non-null via its
// [ModuleInitializer], so these are the only tests in the solution that actually exercise it.
// Port 0 everywhere a real bind is needed, so parallel test runs never collide on a fixed port.
// Settings come from a real rdbsettings.json written per test (WriteConfig below), never CLI flags.
public class NetworkHostTests {
    private string dirA = "";
    private string dirB = "";

    [SetUp]
    public void SetUp() {
        dirA = Path.Combine(Path.GetTempPath(), "rhinodb-networkhost-tests", Guid.NewGuid().ToString("N"), "a");
        dirB = Path.Combine(Path.GetTempPath(), "rhinodb-networkhost-tests", Guid.NewGuid().ToString("N"), "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(Path.GetDirectoryName(dirA)!, recursive: true); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(Path.GetDirectoryName(dirB)!, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class FakeDb(ColdStore cold) : DbContext(cold);

    static private void WriteConfig(string directory, HostConfig host) {
        var json = JsonSerializer.Serialize(new RhinoDbConfig { Host = host }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, GeneratorConfigLoader.ConfigFileName), json);
    }

    [Test]
    public async Task BuildAsync_InRunModeWithHttpEnabled_StartsARealServerAnsweringHealth() {
        WriteConfig(dirA, new HostConfig { ColdPath = dirA, HttpPort = 0 });
        var hostResult = await RhinoHostBuilder.Create(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        using var host = hostResult.Unwrap();
        Assert.That(host.NetworkPort, Is.Not.Null, "RhinoRunMode.Run defaults http-enabled to true, and RhinoDB.Lib.Server is referenced here.");

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{host.NetworkPort}/health");

        Assert.That(response.IsSuccessStatusCode, Is.True);
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_WithHttpExplicitlyDisabled_StartsNoNetworkHostEvenInRunMode() {
        WriteConfig(dirA, new HostConfig { ColdPath = dirA, HttpEnabled = false });
        var hostResult = await RhinoHostBuilder.Create(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
            })
            .BuildAsync();

        Assert.That(hostResult.IsOk(), Is.True);
        using var host = hostResult.Unwrap();
        Assert.That(host.NetworkPort, Is.Null);
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task RhinoHost_Dispose_WithANetworkHostRunning_IsSafeToCallTwiceAndStopsTheServer() {
        WriteConfig(dirA, new HostConfig { ColdPath = dirA, HttpPort = 0 });
        var hostResult = await RhinoHostBuilder.Create(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
            })
            .BuildAsync();

        var host = hostResult.Unwrap();
        var port = host.NetworkPort!.Value;

        Assert.DoesNotThrow(host.Dispose);
        Assert.DoesNotThrow(host.Dispose, "Dispose must be idempotent for the network host too.");

        // A stopped listener doesn't always answer with an immediate RST (connection refused) - on
        // some platforms/timings the SYN is just dropped and the client sits until its own timeout.
        // Either way proves the same thing: nothing is listening anymore, not just "marked disposed."
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        Assert.CatchAsync(async () => await client.GetAsync($"http://127.0.0.1:{port}/health"),
            "the server must actually be stopped, not just marked disposed.");
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task BuildAsync_WhenTheRequestedPortIsAlreadyBound_FailsWithAResultErrorNotAnUnhandledException() {
        WriteConfig(dirA, new HostConfig { ColdPath = dirA, HttpPort = 0 });
        var firstResult = await RhinoHostBuilder.Create(dirA)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
            })
            .BuildAsync();
        using var firstHost = firstResult.Unwrap();
        var boundPort = firstHost.NetworkPort!.Value;

        WriteConfig(dirB, new HostConfig { ColdPath = dirB, HttpPort = boundPort });
        var secondResult = await RhinoHostBuilder.Create(dirB)
            .AddDatabase<FakeDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new FakeDb(cold);
            })
            .BuildAsync();

        Assert.That(secondResult.IsError(), Is.True, "a port already in use must surface as Result.Error, never an unhandled exception.");
        firstHost.GetDatabase<FakeDb>().Cold!.Dispose();
    }
}
