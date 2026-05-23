using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Scout.Host.Worker.Tests.Helpers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OllamaProviderServiceRestartTests
{
    private const string OllamaPath = "/usr/local/bin/ollama";

    private static IOptions<OllamaProviderOptions> Opts() =>
        Options.Create(new OllamaProviderOptions
        {
            Endpoint = "http://localhost:11434",
            PingTimeout = TimeSpan.FromMilliseconds(200),
            StartTimeout = TimeSpan.FromSeconds(2),
        });

    private static OllamaProviderService NewService(
        IBinaryLocator locator,
        IOllamaApiClient api,
        IProcessLauncher launcher,
        ILocalRuntimeRegistry registry,
        ILocalRuntimeConfigStore store,
        IOllamaReadinessProbe? probe = null) =>
        new(
            locator,
            api,
            launcher,
            registry,
            store,
            Opts(),
            probe ?? FakeReadinessProbes.AlwaysReady(),
            NullLogger<OllamaProviderService>.Instance);

    private static ILocalRuntimeConfigStore DefaultStore()
    {
        var s = Substitute.For<ILocalRuntimeConfigStore>();
        s.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        return s;
    }

    [Fact]
    public async Task RestartAsync_ManagedByWorker_StopsThenStarts()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));

        // Existing tracked process.
        var existing = Substitute.For<ILaunchedProcess>();
        existing.Pid.Returns(111);
        existing.HasExited.Returns(false);
        existing.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Sequence: initial Stop sees running (call 1), poll after kill sees
        // gone (call 2), refresh status post-stop (call 3, still gone), then
        // Start re-enters: pre-gate unreachable (4), in-gate unreachable (5),
        // readiness poll succeeds (6), final status (7+).
        var apiCalls = 0;
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            apiCalls++;
            // First call sees the daemon running; everything after is gone
            // until the spawn brings it back. To keep the test deterministic
            // we treat call 1 as running and 2-5 as gone (so stop polls find
            // it gone immediately, then start sees unreachable and spawns),
            // then 6+ as reachable.
            if (apiCalls == 1)
            {
                return Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>()));
            }
            if (apiCalls <= 5)
            {
                return Task.FromResult<OllamaTags?>(null);
            }
            return Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>()));
        });

        var fresh = Substitute.For<ILaunchedProcess>();
        fresh.Pid.Returns(222);
        fresh.HasExited.Returns(false);
        var launcher = Substitute.For<IProcessLauncher>();
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fresh);

        var registry = new InMemoryLocalRuntimeRegistry();
        registry.Track("ollama", existing);

        var service = NewService(locator, api, launcher, registry, DefaultStore());

        var status = await service.RestartAsync(CancellationToken.None);

        status.Running.ShouldBeTrue();
        await existing.Received(1).StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        launcher.Received(1).Launch(Arg.Is<ProcessLaunchRequest>(r =>
            r.FileName == OllamaPath && r.Arguments.Count == 1 && r.Arguments[0] == "serve"));
        registry.GetOwned("ollama").ShouldBeSameAs(fresh);
    }

    [Fact]
    public async Task RestartAsync_External_StopThenStart()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));

        var apiCalls = 0;
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            apiCalls++;
            // Stop path: 1 sees running. 2 (poll after pkill) sees gone.
            // Stop's final refresh: 3 gone.
            // Start path: 4 gone, 5 gone (in-gate), 6 reachable.
            if (apiCalls == 1)
            {
                return Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>()));
            }
            if (apiCalls <= 5)
            {
                return Task.FromResult<OllamaTags?>(null);
            }
            return Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>()));
        });

        var killer = Substitute.For<ILaunchedProcess>();
        killer.HasExited.Returns(true);
        killer.WaitForExitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(0));

        var fresh = Substitute.For<ILaunchedProcess>();
        fresh.HasExited.Returns(false);

        var launcher = Substitute.For<IProcessLauncher>();
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(killer, fresh);

        var registry = new InMemoryLocalRuntimeRegistry();

        var service = NewService(locator, api, launcher, registry, DefaultStore());

        var status = await service.RestartAsync(CancellationToken.None);

        status.Running.ShouldBeTrue();
        // First launch should be the pkill/taskkill helper.
        var expectedKillCmd = OperatingSystem.IsWindows() ? "taskkill" : "pkill";
        launcher.Received().Launch(Arg.Is<ProcessLaunchRequest>(r => r.FileName == expectedKillCmd));
        // Second launch should be the daemon itself.
        launcher.Received().Launch(Arg.Is<ProcessLaunchRequest>(r => r.FileName == OllamaPath));
    }
}
