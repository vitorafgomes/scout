using NSubstitute;
using Scout.ControlPlane.Providers;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Providers;

/// <summary>
/// Direct unit coverage for the <see cref="ProvidersService"/> domain facade:
/// id validation, LM Studio stub semantics and unsupported-lifecycle branching.
/// </summary>
public sealed class ProvidersServiceTests
{
    [Fact]
    public async Task ListAsync_ReturnsOllamaAndLmStudioStub()
    {
        var harness = new OllamaTestHarness();
        var service = harness.BuildProviders();

        var statuses = await service.ListAsync(CancellationToken.None);

        statuses.Select(s => s.Id).ShouldBe(new[]
        {
            ProvidersService.OllamaId,
            ProvidersService.LmStudioId,
        });
    }

    [Fact]
    public async Task GetStatusAsync_UnknownId_Throws()
    {
        var service = new OllamaTestHarness().BuildProviders();

        await Should.ThrowAsync<ProviderUnknownException>(() =>
            service.GetStatusAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task GetStatusAsync_LmStudio_ReturnsUnmanagedStub()
    {
        var service = new OllamaTestHarness().BuildProviders();

        var status = await service.GetStatusAsync(ProvidersService.LmStudioId, CancellationToken.None);

        status.Id.ShouldBe(ProvidersService.LmStudioId);
        status.ManagedByWorker.ShouldBeFalse();
        status.SupportsLifecycle.ShouldBeFalse();
        status.Endpoint.ShouldBe("http://localhost:1234");
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task Lifecycle_OnLmStudio_ThrowsUnsupported(string op)
    {
        var service = new OllamaTestHarness().BuildProviders();

        Task Act() => op switch
        {
            "start" => service.StartAsync(ProvidersService.LmStudioId, CancellationToken.None),
            "stop" => service.StopAsync(ProvidersService.LmStudioId, CancellationToken.None),
            _ => service.RestartAsync(ProvidersService.LmStudioId, CancellationToken.None),
        };

        await Should.ThrowAsync<ProviderLifecycleUnsupportedException>(Act);
    }

    [Fact]
    public async Task ModelOps_OnLmStudio_ThrowUnsupported()
    {
        var service = new OllamaTestHarness().BuildProviders();

        await Should.ThrowAsync<ProviderLifecycleUnsupportedException>(() =>
            service.ListModelsAsync(ProvidersService.LmStudioId, CancellationToken.None));
        await Should.ThrowAsync<ProviderLifecycleUnsupportedException>(() =>
            service.DeleteModelAsync(ProvidersService.LmStudioId, "m", CancellationToken.None));
        Should.Throw<ProviderLifecycleUnsupportedException>(() =>
            service.PullModelAsync(ProvidersService.LmStudioId, "m", CancellationToken.None));
    }

    [Fact]
    public async Task SetAutostart_OnLmStudio_IsNoOp_DoesNotThrow()
    {
        var harness = new OllamaTestHarness();
        var service = harness.BuildProviders();

        await Should.NotThrowAsync(() =>
            service.SetAutostartAsync(ProvidersService.LmStudioId, enabled: true, CancellationToken.None));

        // The Ollama config store must not be touched for the LM Studio toggle.
        await harness.ConfigStore.DidNotReceive()
            .SetAutostartAsync("ollama", Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAutostart_OnOllama_PersistsFlag()
    {
        var harness = new OllamaTestHarness();
        var service = harness.BuildProviders();

        await service.SetAutostartAsync(ProvidersService.OllamaId, enabled: false, CancellationToken.None);

        await harness.ConfigStore.Received(1)
            .SetAutostartAsync("ollama", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatusAsync_OllamaUnreachable_ReportsNotRunning()
    {
        var harness = new OllamaTestHarness();
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(null));
        var service = harness.BuildProviders();

        var status = await service.GetStatusAsync(ProvidersService.OllamaId, CancellationToken.None);

        status.Running.ShouldBeFalse();
        status.LastError.ShouldNotBeNull();
        status.LastError!.Code.ShouldBe("providers/unreachable");
    }
}
