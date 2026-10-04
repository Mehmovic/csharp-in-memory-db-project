using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;

namespace RhinoDB.Sandbox.Lib.Server;

static internal class Program {
    private sealed class SandboxDb(ColdStore cold) : DbContext(cold);

    static private async Task<int> Main() {
        var hostResult = await RhinoHostBuilder.Create()
            .AddDatabase<SandboxDb, DefaultTransaction>(options => {
                options.CreateDb = cold => new SandboxDb(cold);
            })
            .BuildAsync();

        if (hostResult.IsError()) {
            await Console.Error.WriteLineAsync($"Failed to start: {hostResult.GetError().ToException().Message}");
            return 1;
        }

        using var host = hostResult.Unwrap();
        Console.WriteLine(host.NetworkPort is { } port
            ? $"RhinoDB listening on http://127.0.0.1:{port} - try GET /health. Press Ctrl+C to stop."
            : "RhinoDB running with no network host attached (reference RhinoDB.Lib.Server to enable it).");

        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
