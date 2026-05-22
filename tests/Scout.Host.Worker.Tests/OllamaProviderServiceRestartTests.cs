using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.Host.Worker.Providers;
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
        ILocalRuntimeConfigStore store) =>
        new(
            locator,
            api,
            launcher,
            registry,
            store,
            Opts(),
            NullLogger<OllamaProviderService>.Instance);

    private static ILocalRuntimeConfigStore DefaultStore()
    {
        var s = Substitute.For<ILocalRuntimeConfigStore>();
        s.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        return s;
    }

    [Fact]
    public async Task RestartAsync_NotManagedByWorker_ThrowsProviderNotManaged()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));
        var api = Substitute.For<IOllamaApiClient>();
        var launcher = Substitute.For<IProcessLauncher>();
        var registry = new InMemoryLocalRuntimeRegistry();

        var service = NewService(locator, api, launcher, registry, DefaultStore());

        await Should.ThrowAsync<ProviderNotManagedException>(
            () => service.RestartAsync(CancellationToken.None));
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

        // After Stop+Untrack, StartAsync's GetStatusAsync sees the daemon as
        // unreachable, spawns a fresh process, and polls until reachable.
        var apiCalls = 0;
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            apiCalls++;
            // First two: pre-gate + in-gate status see unreachable.
            // Subsequent: poll succeeds.
            return Task.FromResult<OllamaTags?>(apiCalls >= 3
                ? new OllamaTags(Array.Empty<ProviderModel>())
                : null);
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
        launcher.Received(1).Launch(Arg.Any<ProcessLaunchRequest>());
        registry.GetOwned("ollama").ShouldBeSameAs(fresh);
    }

    [Fact]
    public async Task RestartAsync_StopGraceExpires_LogsAndStillStarts()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));

        var existing = Substitute.For<ILaunchedProcess>();
        existing.Pid.Returns(111);
        existing.HasExited.Returns(false);
        // StopAsync returns false (force-killed) to simulate the grace
        // window expiring.
        existing.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var apiCalls = 0;
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            apiCalls++;
            return Task.FromResult<OllamaTags?>(apiCalls >= 3
                ? new OllamaTags(Array.Empty<ProviderModel>())
                : null);
        });

        var fresh = Substitute.For<ILaunchedProcess>();
        fresh.HasExited.Returns(false);
        var launcher = Substitute.For<IProcessLauncher>();
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fresh);

        var registry = new InMemoryLocalRuntimeRegistry();
        registry.Track("ollama", existing);

        var service = NewService(locator, api, launcher, registry, DefaultStore());

        var status = await service.RestartAsync(CancellationToken.None);

        status.Running.ShouldBeTrue();
        await existing.Received(1).StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        launcher.Received(1).Launch(Arg.Any<ProcessLaunchRequest>());
    }
}
