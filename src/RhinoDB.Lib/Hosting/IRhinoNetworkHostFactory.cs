namespace RhinoDB.Lib.Hosting;

public interface IRhinoNetworkHost : IAsyncDisposable {
    int Port { get; }
}

public interface IRhinoNetworkHostFactory {
    Task<Result<IRhinoNetworkHost>> StartAsync(RhinoHost host, int port, CancellationToken ct);
}
