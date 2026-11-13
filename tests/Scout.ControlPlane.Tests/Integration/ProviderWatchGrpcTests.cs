using Grpc.Core;
using Scout.ControlPlane.Contracts;
using Shouldly;
using Xunit;
using GrpcProviders = Scout.ControlPlane.Contracts.Providers;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// The provider status watch over the real UDS host.
/// </summary>
[Collection("uds")]
public sealed class ProviderWatchGrpcTests
{
    [Theory]
    [InlineData(ProviderId.Unspecified, "Unknown provider 'Unspecified'.")]
    [InlineData((ProviderId)99, "Unknown provider '99'.")]
    public async Task Watching_An_Unknown_Provider_Answers_NotFound(ProviderId id, string detail)
    {
        await using var host = await ControlPlaneTestHost.StartAsync();
        using var channel = host.CreateChannel();
        var client = new GrpcProviders.ProvidersClient(channel);
        using var watch = client.WatchProviderStatus(new WatchProviderStatusRequest { Id = id });

        var refused = await Should.ThrowAsync<RpcException>(() => watch.ResponseStream.MoveNext(CancellationToken.None));

        (refused.StatusCode, refused.Status.Detail).ShouldBe((StatusCode.NotFound, detail));
    }
}
