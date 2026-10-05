using System.Reflection;
using RhinoDB.Core;
using RhinoDB.Lib.Cold;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Realtime;
using RhinoDB.Sandbox.Benchmark.Schema;

namespace RhinoDB.Sandbox.Benchmark.Benchmarks;

internal sealed class BenchHost {
    public string Directory { get; }
    public RhinoHost Host { get; }
    public RhinoCtx Ctx { get; }

    private BenchHost(string directory, RhinoHost host) {
        Directory = directory;
        Host = host;
        Ctx = new RhinoCtx(host, Identity.Anonymous);
    }

    static public string NewDirectory() {
        var directory = Path.Combine(Path.GetTempPath(), "rhinodb-bench", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var coldPath = directory.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(directory, "rdbsettings.json"),
            $$"""{ "Host": { "ColdPath": "{{coldPath}}", "Mode": "run", "HttpEnabled": false, "HttpPort": 0 } }""");
        return directory;
    }

    static public BenchHost Start(string directory) {
        var host = RhinoHostBuilder.Create(directory)
            .AddGeneratedChildDatabases()
            .AddDatabase<HostRootDb, HostRootDbTransaction>(o => o.CreateDb = cold => new HostRootDb(cold))
            .BuildAsync().GetAwaiter().GetResult().Unwrap();
        new HostRootDbLoader().LoadAsync(host.GetDatabase<HostRootDb>()).GetAwaiter().GetResult();
        return new BenchHost(directory, host);
    }

    public MatchDb Match(int key) =>
        Host.GetOrActivateChildAsync<MatchDb, MatchDbTransaction, int>(key).GetAwaiter().GetResult().Unwrap();

    public void Stop() {
        var root = Host.GetDatabase<HostRootDb>();
        var cold = (ColdStore)typeof(DbContext<HostRootDbTransaction>)
            .GetProperty("Cold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
        Host.Dispose();
        cold.Dispose();
    }

    public void StopAndDelete() {
        Stop();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
