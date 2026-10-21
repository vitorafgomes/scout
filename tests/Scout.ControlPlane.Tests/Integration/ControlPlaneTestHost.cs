using System.Net.Sockets;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Providers;
using Scout.Host.ControlPlane;
using Scout.Host.ControlPlane.Security;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// Boots the gRPC control-plane host on a temp Unix domain socket with a pinned
/// session token and fully stubbed Ollama backend (no network, no daemon). The
/// host binds only the UDS - never a TCP port. Disposing tears the host down and
/// removes the temp directory.
/// </summary>
internal sealed class ControlPlaneTestHost : IAsyncDisposable
{
    public const string Token = "integration-session-token";

    private readonly WebApplication _app;
    private readonly string _tempDir;

    public string SocketPath { get; }

    public StubOllamaApiClient Ollama { get; }

    /// <summary>Addresses Kestrel reports as bound. For a UDS-only host this is empty (no TCP).</summary>
    public IReadOnlyCollection<string> BoundAddresses
    {
        get
        {
            var feature = _app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features
                .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
            return feature?.Addresses.ToArray() ?? Array.Empty<string>();
        }
    }

    private ControlPlaneTestHost(
        WebApplication app,
        string socketPath,
        string tempDir,
        StubOllamaApiClient ollama)
    {
        _app = app;
        SocketPath = socketPath;
        _tempDir = tempDir;
        Ollama = ollama;
    }

    public static async Task<ControlPlaneTestHost> StartAsync(
        Action<StubOllamaApiClient>? configureOllama = null)
    {
        var tempDir = Directory.CreateTempSubdirectory("scout-cp-it-").FullName;
        var socketPath = Path.Combine(tempDir, "controlplane.sock");
        var ollama = new StubOllamaApiClient();
        configureOllama?.Invoke(ollama);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        // Drop the default http://localhost:5000 preference; the only listener
        // is the UDS configured below, so the addresses feature must stay empty.
        builder.WebHost.UseUrls(Array.Empty<string>());
        builder.WebHost.UseSetting(
            Microsoft.AspNetCore.Hosting.WebHostDefaults.PreferHostingUrlsKey, "false");

        // Pin all on-disk state into the test's temp dir so nothing escapes it.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Features:StateStore:FilePath"] = Path.Combine(tempDir, "features.json"),
                ["Scout:LocalRuntime:Config:FilePath"] = Path.Combine(tempDir, "local-runtimes.json"),
            })
            .Build();
        builder.Services.AddControlPlaneHost(config, SessionToken.FromValue(Token));

        // Permissive activator so enable/disable round-trips actually toggle state.
        builder.Services.RemoveAll<IFeatureActivator>();
        builder.Services.AddSingleton<IFeatureActivator>(new StubFeatureActivator());

        // Available credential store so credential persistence round-trips.
        // The default in-memory store reports IsAvailable=false and fails closed,
        // which is asserted separately in the unit tests.
        builder.Services.RemoveAll<ICredentialStore>();
        builder.Services.AddSingleton<ICredentialStore>(new InMemoryCredentialStore(isAvailable: true));

        // Replace the live Ollama backend seams with hermetic stubs.
        builder.Services.RemoveAll<IOllamaApiClient>();
        builder.Services.AddSingleton<IOllamaApiClient>(ollama);
        builder.Services.RemoveAll<IBinaryLocator>();
        builder.Services.AddSingleton<IBinaryLocator>(new FakeBinaryLocator("/usr/bin/ollama"));
        builder.Services.RemoveAll<IOllamaReadinessProbe>();
        builder.Services.AddSingleton<IOllamaReadinessProbe>(new AlwaysReadyProbe());

        builder.Services.Configure<KestrelServerOptions>(kestrel =>
        {
            kestrel.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2);
        });

        var app = builder.Build();
        app.MapControlPlaneServices();
        await app.StartAsync().ConfigureAwait(false);

        return new ControlPlaneTestHost(app, socketPath, tempDir, ollama);
    }

    /// <summary>Open a gRPC channel over the host's UDS with the session token attached.</summary>
    public GrpcChannel CreateChannel(bool withToken = true) =>
        CreateChannelFor(SocketPath, withToken ? Token : null);

    /// <summary>Open a gRPC channel over an arbitrary UDS path (used for failure injection).</summary>
    public static GrpcChannel CreateChannelFor(string socketPath, string? token)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        var options = new GrpcChannelOptions { HttpHandler = handler };
        if (token is not null)
        {
            var credentials = Grpc.Core.CallCredentials.FromInterceptor((_, metadata) =>
            {
                metadata.Add("x-scout-token", token);
                return Task.CompletedTask;
            });
            options.Credentials = Grpc.Core.ChannelCredentials.Create(
                Grpc.Core.ChannelCredentials.Insecure, credentials);
            options.UnsafeUseInsecureChannelCallCredentials = true;
        }

        // The authority is irrelevant for UDS; use a stable placeholder.
        return GrpcChannel.ForAddress("http://localhost", options);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort teardown; the temp dir is removed regardless.
        }

        await _app.DisposeAsync().ConfigureAwait(false);

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private sealed class FakeBinaryLocator : IBinaryLocator
    {
        private readonly string? _path;

        public FakeBinaryLocator(string? path) => _path = path;

        public Task<string?> FindOnPathAsync(string binaryName, CancellationToken ct) =>
            Task.FromResult(_path);
    }
}
