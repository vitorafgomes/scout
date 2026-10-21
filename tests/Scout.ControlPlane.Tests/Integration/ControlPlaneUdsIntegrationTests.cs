using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Scout.ControlPlane.Features;
using Scout.ControlPlane.Providers;
using Shouldly;
using Xunit;
using GrpcFeatures = Scout.ControlPlane.Contracts.Features;
using GrpcModels = Scout.ControlPlane.Contracts.Models;
using GrpcProviders = Scout.ControlPlane.Contracts.Providers;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// End-to-end coverage over a real Unix-domain-socket gRPC channel: the host is
/// booted on a temp UDS with a pinned token and stubbed Ollama backend, and the
/// client talks to it through a SocketsHttpHandler ConnectCallback.
/// </summary>
[Collection("uds")]
public sealed class ControlPlaneUdsIntegrationTests
{
    [Fact]
    public async Task ListProviders_OverUds_ReturnsOllamaAndLmStudio()
    {
        await using var host = await ControlPlaneTestHost.StartAsync(
            ollama => ollama.Tags = new OllamaTags(new[]
            {
                new Scout.ControlPlane.Providers.ProviderModel("llama3", 1024, "2024-01-01"),
            }));
        using var channel = host.CreateChannel();
        var client = new GrpcProviders.ProvidersClient(channel);

        var response = await client.ListProvidersAsync(new ListProvidersRequest());

        response.Providers.Select(p => p.Id)
            .ShouldBe(new[] { ProviderId.Ollama, ProviderId.LmStudio });
        var ollama = response.Providers.Single(p => p.Id == ProviderId.Ollama);
        ollama.Running.ShouldBeTrue();
        ollama.Models.Select(m => m.Name).ShouldContain("llama3");
    }

    [Fact]
    public async Task NoTcpListenerExists_AfterStartup()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();

        // The UDS file is the only bound endpoint.
        File.Exists(host.SocketPath).ShouldBeTrue();

        // No TCP listener is actually accepting connections: every TCP address
        // the host advertises must refuse a connect (Kestrel binds only the UDS,
        // any lingering localhost URL is an unconsumed default, not a listener).
        foreach (var address in host.BoundAddresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                continue;
            }

            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await Should.ThrowAsync<System.Net.Sockets.SocketException>(async () =>
                await socket.ConnectAsync(
                    System.Net.IPAddress.Loopback, uri.Port, cts.Token));
        }
    }

    [Fact]
    public async Task FeatureEnableDisable_RoundTrips_OverUds()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel();
        var client = new GrpcFeatures.FeaturesClient(channel);

        // Seed the required credential so Enable is not blocked.
        var setCreds = new SetCredentialsRequest { Id = FeatureCatalog.GitHubId };
        setCreds.Credentials.Add("pat", "ghp_dummy_token_value");
        var afterCreds = await client.SetCredentialsAsync(setCreds);
        afterCreds.HasCredentials.ShouldBeTrue();

        var enabled = await client.EnableAsync(new EnableRequest { Id = FeatureCatalog.GitHubId });
        enabled.Enabled.ShouldBeTrue();
        enabled.Status.ShouldBe(FeatureStatus.Enabled);

        var disabled = await client.DisableAsync(new DisableRequest { Id = FeatureCatalog.GitHubId });
        disabled.Enabled.ShouldBeFalse();
        disabled.Status.ShouldBe(FeatureStatus.Disabled);
    }

    [Fact]
    public async Task PullModel_ServerStream_ConsumedToCompletion()
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel();
        var client = new GrpcModels.ModelsClient(channel);

        using var call = client.PullModel(new PullModelRequest
        {
            Provider = ProviderId.Ollama,
            Model = "llama3",
        });

        // Feed three frames then close the stream from the backend side.
        await host.Ollama.EmitAsync(new PullProgress("pulling manifest", null, 100, 0));
        await host.Ollama.EmitAsync(new PullProgress("downloading", null, 100, 50));
        await host.Ollama.EmitAsync(new PullProgress("success", null, 100, 100));
        host.Ollama.CompletePull();

        var frames = new List<ModelPullProgress>();
        await foreach (var frame in call.ResponseStream.ReadAllAsync())
        {
            frames.Add(frame);
        }

        frames.Count.ShouldBe(3);
        frames[^1].Status.ShouldBe("success");
        frames[^1].Percent.ShouldBe(100d);
        host.Ollama.PullCompleted.ShouldBeTrue();
    }
}
