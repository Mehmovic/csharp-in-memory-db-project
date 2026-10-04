using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using RhinoDB.Core;
using RhinoDB.Lib.Hosting;
using RhinoDB.Lib.Server.Realtime;

namespace RhinoDB.Lib.Server;

static internal class NetworkHostModuleInit {
    // CA2255: intentional - this IS the plugin-auto-registration seam (RhinoNetworkHostProvider's own
    // doc comment), not application startup code triggering it by accident.
#pragma warning disable CA2255
    [ModuleInitializer]
    static internal void Register() => RhinoNetworkHostProvider.Factory = new NetworkHostFactory();
#pragma warning restore CA2255
}

internal sealed class NetworkHostFactory : IRhinoNetworkHostFactory {
    public async Task<Result<IRhinoNetworkHost>> StartAsync(RhinoHost host, int port, CancellationToken ct) {
        try {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

            var app = builder.Build();
            app.MapGet("/health", () => Results.Ok());

            var transport = new WsTransport(host);
            app.UseWebSockets();
            app.Map("/rt", async (HttpContext context) => {
                if (!context.WebSockets.IsWebSocketRequest) {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                var socket = await context.WebSockets.AcceptWebSocketAsync();
                await transport.AcceptConnectionAsync(socket, context.RequestAborted);
            });

            foreach (var (method, route) in host.RegisteredRestCommands) {
                app.MapMethods(route, [method], async (HttpContext context) => {
                    using var bodyStream = new MemoryStream();
                    await context.Request.Body.CopyToAsync(bodyStream, context.RequestAborted);
                    var query = context.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
                    var request = new RestRequest(method, route, query, bodyStream.ToArray());

                    var response = await host.DispatchRestAsync(method, route, request, context.RequestAborted);

                    context.Response.StatusCode = response.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
                });
            }

            await app.StartAsync(ct);

            return Result<IRhinoNetworkHost>.Ok(new NetworkHost(app, ResolveBoundPort(app, port)));
        } catch (Exception ex) {
            return Result<IRhinoNetworkHost>.Error(DbError.SystemFailure(ex));
        }
    }

    static private int ResolveBoundPort(WebApplication app, int requestedPort) {
        if (requestedPort != 0) return requestedPort;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return address is null ? requestedPort : new Uri(address).Port;
    }
}

internal sealed class NetworkHost(WebApplication app, int port) : IRhinoNetworkHost {
    public int Port { get; } = port;

    public async ValueTask DisposeAsync() {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
