using System.Text;
using System.Text.Json;

using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.SchemaContracts;

namespace RhinoDB.Lib.Server.Test;

// Real HttpClient, real dispatch through RhinoHostBuilder.AddRestCommand, real Kestrel - same bar as
// WsTransportTests.cs. RestRequest/RestResponse are plain records with no ASP.NET Core type in them;
// NetworkHost.cs is the only file translating a real HttpContext into/out of that shape.
public class RestCommandCenterTests {
    private string dir = "";

    [SetUp]
    public void SetUp() {
        dir = Path.Combine(Path.GetTempPath(), "rhinodb-restcommandcenter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class FakeDb(ColdStore cold) : DbContext(cold);

    static private void WriteConfig(string directory, int httpPort) {
        var json = JsonSerializer.Serialize(
            new RhinoDbConfig { Host = new HostConfig { ColdPath = directory, HttpPort = httpPort } },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, GeneratorConfigLoader.ConfigFileName), json);
    }

    private async Task<RhinoHost> BuildHostAsync(RhinoHostBuilder builder) {
        WriteConfig(dir, 0);
        var hostResult = await builder
            .AddDatabase<FakeDb, DefaultTransaction>(options => options.CreateDb = cold => new FakeDb(cold))
            .BuildAsync();
        return hostResult.Unwrap();
    }

    [Test]
    public async Task RegisteredGetRoute_DispatchesAndEchoesTheQueryParamBack() {
        var builder = RhinoHostBuilder.Create(dir).AddRestCommand("GET", "/echo", (_, request, _) => {
            var name = request.Query.TryGetValue("name", out var value) ? value : "";
            return Task.FromResult(RestResponse.Ok(Encoding.UTF8.GetBytes($"{{\"name\":\"{name}\"}}")));
        });

        using var host = await BuildHostAsync(builder);
        using var client = new HttpClient();

        var response = await client.GetAsync($"http://127.0.0.1:{host.NetworkPort}/echo?name=ada");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
        Assert.That(body, Is.EqualTo("{\"name\":\"ada\"}"));
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task RegisteredPostRoute_ReceivesTheRequestBody() {
        var builder = RhinoHostBuilder.Create(dir).AddRestCommand("POST", "/echo-body", (_, request, _) =>
            Task.FromResult(RestResponse.Ok(request.Body)));

        using var host = await BuildHostAsync(builder);
        using var client = new HttpClient();

        var response = await client.PostAsync($"http://127.0.0.1:{host.NetworkPort}/echo-body", new StringContent("{\"x\":1}"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(body, Is.EqualTo("{\"x\":1}"));
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }

    [Test]
    public async Task UnregisteredRoute_RespondsNotFound() {
        using var host = await BuildHostAsync(RhinoHostBuilder.Create(dir));
        using var client = new HttpClient();

        var response = await client.GetAsync($"http://127.0.0.1:{host.NetworkPort}/does-not-exist");

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));
        host.GetDatabase<FakeDb>().Cold!.Dispose();
    }
}
