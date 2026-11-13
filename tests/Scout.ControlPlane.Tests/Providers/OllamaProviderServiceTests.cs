using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
    public async Task ListModelsAsync_ExternalDaemon_ColdStart_SucceedsWithoutPriorStart()
    {
        // Cold start: the worker never ran Start, so the readiness gate is
        // still Unknown and never completes (WaitReadyAsync would time out).
        // Ollama is already running externally, so /api/tags answers and the
        // op must proceed instead of throwing ProviderNotReadyException.
        var harness = new OllamaTestHarness();
        harness.Probe.Current.Returns(OllamaReadinessState.Unknown);
        harness.Probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(new[]
            {
                new ProviderModel("phi3", 7, "2024-03-03"),
            })));
        var service = harness.Build();

        var models = await service.ListModelsAsync(CancellationToken.None);

        models.Single().Name.ShouldBe("phi3");
        await harness.Probe.DidNotReceive()
            .WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListModelsAsync_ColdStart_DaemonUnreachable_StillGatesOnProbe()
    {
        // Unknown gate but daemon not reachable: fall through to the probe so
        // a genuinely-not-ready runtime still surfaces ProviderNotReadyException.
        var harness = new OllamaTestHarness();
        harness.Probe.Current.Returns(OllamaReadinessState.Unknown);
        harness.Probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(null));
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

    [Fact]
    public async Task StopAsync_ExternalDaemon_DoesNotKill_ReturnsExternallyManaged()
    {
        // Daemon is running but Scout does not own it (registry empty). Scout
        // must not kill an unowned process; it surfaces a clear status instead.
        var harness = new OllamaTestHarness();
        harness.Registry.GetOwned("ollama").Returns((ILaunchedProcess?)null);
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>())));
        var service = harness.Build();

        var status = await service.StopAsync(CancellationToken.None);

        status.Running.ShouldBeTrue();
        status.LastError.ShouldNotBeNull();
        status.LastError!.Code.ShouldBe("providers/externally-managed");

        // No pkill/taskkill: the launcher must never be invoked on this path.
        harness.Launcher.DidNotReceive().Launch(Arg.Any<ProcessLaunchRequest>());
    }

    [Fact]
    public async Task StopAsync_OwnedDaemon_StopsAndUntracks()
    {
        var harness = new OllamaTestHarness();
        var owned = Substitute.For<ILaunchedProcess>();
        owned.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        harness.Registry.GetOwned("ollama").Returns(owned);

        // Running on entry, gone after the stop so the poll exits promptly.
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>())),
                Task.FromResult<OllamaTags?>(null));
        var service = harness.Build();

        await service.StopAsync(CancellationToken.None);

        await owned.Received(1).StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        harness.Registry.Received(1).Untrack("ollama");
        harness.Launcher.DidNotReceive().Launch(Arg.Any<ProcessLaunchRequest>());
    }

    [Fact]
    public async Task StopAsync_NotRunning_ShortCircuits()
    {
        var harness = new OllamaTestHarness();
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(null));
        var service = harness.Build();

        var status = await service.StopAsync(CancellationToken.None);

        status.Running.ShouldBeFalse();
        harness.Launcher.DidNotReceive().Launch(Arg.Any<ProcessLaunchRequest>());
    }

    [Fact]
    public async Task StartAsync_ScrubsSessionTokenFromChildEnvironment()
    {
        // Daemon not running yet, so StartCore spawns. The launch request must
        // remove SCOUT_CONTROLPLANE_TOKEN so it is not inherited by the child.
        var harness = new OllamaTestHarness();
        var spawned = Substitute.For<ILaunchedProcess>();
        spawned.HasExited.Returns(false);
        ProcessLaunchRequest? captured = null;
        harness.Launcher.Launch(Arg.Do<ProcessLaunchRequest>(r => captured = r)).Returns(spawned);

        // Not running on the pre-spawn checks, reachable once after spawn.
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<OllamaTags?>(null),
                Task.FromResult<OllamaTags?>(null),
                Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>())));
        var service = harness.Build();

        await service.StartAsync(CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.EnvironmentOverrides.ShouldNotBeNull();
        captured.EnvironmentOverrides!.ContainsKey(OllamaProviderService.SessionTokenEnvVarName).ShouldBeTrue();
        captured.EnvironmentOverrides[OllamaProviderService.SessionTokenEnvVarName].ShouldBeNull();
        captured.EnvironmentOverrides.ContainsKey(OllamaProviderService.WorkerTokenEnvVarName).ShouldBeTrue();
        captured.EnvironmentOverrides[OllamaProviderService.WorkerTokenEnvVarName].ShouldBeNull();
    }

    [Theory]
    [InlineData("not installed")]
    [InlineData("not running after the spawn")]
    public async Task A_Start_That_Does_Not_Report_Ollama_Running_Does_Not_Mark_It_Ready(string outcome)
    {
        var harness = new OllamaTestHarness();
        var probe = new OllamaReadinessProbe();
        if (outcome == "not installed")
        {
            harness.Locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));
        }
        else
        {
            var spawned = Substitute.For<ILaunchedProcess>();
            spawned.HasExited.Returns(false);
            harness.Launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(spawned);
            harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<OllamaTags?>(null),
                    Task.FromResult<OllamaTags?>(null),
                    Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>())),
                    Task.FromResult<OllamaTags?>(null));
        }
        var service = new OllamaProviderService(
            harness.Locator,
            harness.Api,
            harness.Launcher,
            harness.Registry,
            harness.ConfigStore,
            Microsoft.Extensions.Options.Options.Create(harness.Options),
            probe,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaProviderService>.Instance);

        var status = await service.StartAsync(CancellationToken.None);

        status.Running.ShouldBeFalse();
        probe.IsReady.ShouldBeFalse();
        probe.Current.ShouldBe(OllamaReadinessState.Failed);
    }

    [Fact]
    public async Task After_A_Start_That_Did_Not_Get_Ollama_Running_Status_And_Models_See_An_Ollama_Started_Later()
    {
        var harness = new OllamaTestHarness();
        var probe = new OllamaReadinessProbe();
        harness.Locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));
        var service = new OllamaProviderService(
            harness.Locator,
            harness.Api,
            harness.Launcher,
            harness.Registry,
            harness.ConfigStore,
            Microsoft.Extensions.Options.Options.Create(harness.Options),
            probe,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaProviderService>.Instance);
        (await service.StartAsync(CancellationToken.None)).Installed.ShouldBeFalse();
        harness.Locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("/usr/bin/ollama"));
        harness.Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags([new ProviderModel("llama3", 10, "2024-01-01")])));

        var status = await service.GetStatusAsync(CancellationToken.None);

        (status.Installed, status.Running, status.SupportsLifecycle, status.LastError).ShouldBe((true, true, true, (ProviderError?)null));
        await Should.NotThrowAsync(() => service.ListModelsAsync(CancellationToken.None));
        (await service.ListModelsAsync(CancellationToken.None)).Select(m => m.Name).ShouldBe(["llama3"]);
    }

    [Theory]
    [InlineData("deleted", LogLevel.Information, "Deleted local model llama3", null, 1)]
    [InlineData("the server answers 500", LogLevel.Warning, "Could not delete local model llama3", typeof(HttpRequestException), 1)]
    [InlineData("the server times out", LogLevel.Warning, "Could not delete local model llama3", typeof(TimeoutException), 1)]
    [InlineData("the server is not ready", LogLevel.Warning, "Could not delete local model llama3", typeof(ProviderNotReadyException), 0)]
    public async Task Deleting_A_Local_Model_Logs_One_Line_Naming_The_Model_And_How_It_Ended(
        string outcome, LogLevel level, string line, Type? reachesTheCaller, int deleteCallsToTheServer)
    {
        var harness = new OllamaTestHarness();
        Exception? failure = outcome switch
        {
            "the server answers 500" => new HttpRequestException("Ollama /api/delete returned 500: model is in use"),
            "the server times out" => new TimeoutException("Ollama /api/delete timed out after 00:00:05."),
            _ => null,
        };
        if (failure is not null)
        {
            harness.Api.DeleteModelAsync("llama3", Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        }
        if (outcome == "the server is not ready")
        {
            harness.Probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        }
        var service = harness.Build();

        var thrown = await Record.ExceptionAsync(() => service.DeleteModelAsync("llama3", CancellationToken.None));

        harness.Logger.Entries.ShouldBe([(level, line)]);
        (thrown?.GetType()).ShouldBe(reachesTheCaller);
        harness.Api.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IOllamaApiClient.DeleteModelAsync))
            .ShouldBe(deleteCallsToTheServer);
    }

    [Theory]
    [InlineData("finished", 0, LogLevel.Information, "Downloaded local model llama3", null, 1)]
    [InlineData("the stream fails after one frame", 0, LogLevel.Warning, "Download of local model llama3 did not finish", typeof(HttpRequestException), 1)]
    [InlineData("the server is not ready", 0, LogLevel.Warning, "Download of local model llama3 did not finish", typeof(ProviderNotReadyException), 0)]
    [InlineData("the caller stops reading after one frame", 1, LogLevel.Warning, "Download of local model llama3 did not finish", null, 1)]
    public async Task Downloading_A_Local_Model_Logs_One_Line_At_The_Start_And_One_At_The_End(
        string outcome, int framesReadBeforeStopping, LogLevel endLevel, string endLine, Type? reachesTheCaller, int pullCallsToTheServer)
    {
        var harness = new OllamaTestHarness();
        harness.Api.PullAsync("llama3", Arg.Any<CancellationToken>())
            .Returns(_ => outcome == "the stream fails after one frame"
                ? OneFrameThen(new HttpRequestException("Ollama /api/pull failed: no space left on device"))
                : Frames(new PullProgress("pulling", null, 100, 0), new PullProgress("success", null, 100, 100)));
        if (outcome == "the server is not ready")
        {
            harness.Probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        }
        var service = harness.Build();

        var thrown = await Record.ExceptionAsync(async () =>
        {
            var read = 0;
            await foreach (var _ in service.PullModelAsync("llama3", CancellationToken.None))
            {
                if (++read == framesReadBeforeStopping)
                {
                    break;
                }
            }
        });

        harness.Logger.Entries.ShouldBe([(LogLevel.Information, "Downloading local model llama3"), (endLevel, endLine)]);
        (thrown?.GetType()).ShouldBe(reachesTheCaller);
        harness.Api.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IOllamaApiClient.PullAsync))
            .ShouldBe(pullCallsToTheServer);
    }

    private static async IAsyncEnumerable<PullProgress> OneFrameThen(Exception failure)
    {
        yield return new PullProgress("pulling", null, 100, 10);
        await Task.CompletedTask;
        throw failure;
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
