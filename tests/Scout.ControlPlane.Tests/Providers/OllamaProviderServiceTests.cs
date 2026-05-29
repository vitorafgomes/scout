using NSubstitute;
using Scout.ControlPlane.Providers;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Providers;

/// <summary>
/// Unit coverage for <see cref="OllamaProviderService"/> model operations and
/// readiness gating, with every collaborator substituted (no real daemon).
/// </summary>
public sealed class OllamaProviderServiceTests
{
    [Fact]
    public async Task GetStatusAsync_InstalledAndRunning_ReportsModels()
    {
        var harness = new OllamaTestHarness();
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(new[]
            {
                new ProviderModel("llama3", 10, "2024-01-01"),
            })));
        var service = harness.Build();

        var status = await service.GetStatusAsync(CancellationToken.None);

        status.Installed.ShouldBeTrue();
        status.Running.ShouldBeTrue();
        status.SupportsLifecycle.ShouldBeTrue();
        status.Models.Single().Name.ShouldBe("llama3");
    }

    [Fact]
    public async Task GetStatusAsync_ProbeNotReady_ReportsUnreachable()
    {
        var harness = new OllamaTestHarness();
        harness.Probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        var service = harness.Build();

        var status = await service.GetStatusAsync(CancellationToken.None);

        status.Running.ShouldBeFalse();
        status.Installed.ShouldBeFalse();
        status.LastError!.Code.ShouldBe("providers/unreachable");
    }

    [Fact]
    public async Task ListModelsAsync_NotReady_Throws()
    {
        var harness = new OllamaTestHarness();
        harness.Probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        var service = harness.Build();

        await Should.ThrowAsync<ProviderNotReadyException>(() =>
            service.ListModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListModelsAsync_Ready_ReturnsTags()
    {
        var harness = new OllamaTestHarness();
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(new[]
            {
                new ProviderModel("mistral", 5, "2024-02-02"),
            })));
        var service = harness.Build();

        var models = await service.ListModelsAsync(CancellationToken.None);

        models.Single().Name.ShouldBe("mistral");
    }

    [Fact]
    public async Task DeleteModelAsync_Ready_DelegatesToApi()
    {
        var harness = new OllamaTestHarness();
        var service = harness.Build();

        await service.DeleteModelAsync("llama3", CancellationToken.None);

        await harness.Api.Received(1).DeleteModelAsync("llama3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PullModelAsync_Ready_StreamsApiFrames()
    {
        var harness = new OllamaTestHarness();
        harness.Api.PullAsync("llama3", Arg.Any<CancellationToken>())
            .Returns(_ => Frames(
                new PullProgress("pulling", null, 100, 0),
                new PullProgress("success", null, 100, 100)));
        var service = harness.Build();

        var frames = new List<PullProgress>();
        await foreach (var f in service.PullModelAsync("llama3", CancellationToken.None))
        {
            frames.Add(f);
        }

        frames.Count.ShouldBe(2);
        frames[^1].Status.ShouldBe("success");
    }

    [Fact]
    public async Task PullModelAsync_NotReady_Throws()
    {
        var harness = new OllamaTestHarness();
        harness.Probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        var service = harness.Build();

        await Should.ThrowAsync<ProviderNotReadyException>(async () =>
        {
            await foreach (var _ in service.PullModelAsync("llama3", CancellationToken.None))
            {
            }
        });
    }

    private static async IAsyncEnumerable<PullProgress> Frames(
        params PullProgress[] frames)
    {
        foreach (var f in frames)
        {
            yield return f;
        }
        await Task.CompletedTask;
    }
}
