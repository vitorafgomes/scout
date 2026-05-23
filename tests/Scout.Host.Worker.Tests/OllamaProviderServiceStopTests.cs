using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Scout.Host.Worker.Tests.Helpers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OllamaProviderServiceStopTests
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

    private static IBinaryLocator InstalledLocator()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));
        return locator;
    }

    [Fact]
    public async Task StopAsync_AlreadyStopped_IsIdempotent()
    {
        var locator = InstalledLocator();
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(null));
        var launcher = Substitute.For<IProcessLauncher>();
        var registry = new InMemoryLocalRuntimeRegistry();

        var service = NewService(locator, api, launcher, registry, DefaultStore());

        var status = await service.StopAsync(CancellationToken.None);

        status.Running.ShouldBeFalse();
        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
    }

    [Fact]
    public async Task StopAsync_Owned_CallsStopOnLaunchedProcess()
    {
        var locator = InstalledLocator();

        var apiCalls = 0;
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            apiCalls++;
            // First call: running. Subsequent: gone.
            return Task.FromResult<OllamaTags?>(apiCalls == 1
                ? new OllamaTags(Array.Empty<ProviderModel>())
                : null);
        });

        var owned = Substitute.For<ILaunchedProcess>();
        owned.Pid.Returns(777);
        owned.HasExited.Returns(false);
        owned.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var launcher = Substitute.For<IProcessLauncher>();
        var registry = new InMemoryLocalRuntimeRegistry();
        registry.Track("ollama", owned);

        var service = NewService(locator, api, launcher, registry, DefaultStore());

        var status = await service.StopAsync(CancellationToken.None);

        status.Running.ShouldBeFalse();
        await owned.Received(1).StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        registry.GetOwned("ollama").ShouldBeNull();
        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
    }

    [Fact]
    public async Task StopAsync_External_SpawnsPkillAndPollsUntilGone()
    {
        var locator = InstalledLocator();

        var apiCalls = 0;
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            apiCalls++;
            // 1: initial status sees running. 2: first poll still running.
            // 3+: gone.
            if (apiCalls == 1)
            {
                return Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>()));
            }
            if (apiCalls == 2)
            {
                return Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>()));
            }
            return Task.FromResult<OllamaTags?>(null);
        });

        var killer = Substitute.For<ILaunchedProcess>();
        killer.HasExited.Returns(true);
        killer.WaitForExitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(0));

        var launcher = Substitute.For<IProcessLauncher>();
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(killer);

        var registry = new InMemoryLocalRuntimeRegistry();
        var service = NewService(locator, api, launcher, registry, DefaultStore());

        var status = await service.StopAsync(CancellationToken.None);

        status.Running.ShouldBeFalse();
        var expected = OperatingSystem.IsWindows() ? "taskkill" : "pkill";
        launcher.Received(1).Launch(Arg.Is<ProcessLaunchRequest>(r => r.FileName == expected));
    }

    [Fact]
    public async Task StopAsync_External_NeverGoesAway_ThrowsStopFailed()
    {
        var locator = InstalledLocator();
        var api = Substitute.For<IOllamaApiClient>();
        // Always reachable, even after pkill.
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>())));

        var killer = Substitute.For<ILaunchedProcess>();
        killer.HasExited.Returns(true);
        killer.WaitForExitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(0));
        var launcher = Substitute.For<IProcessLauncher>();
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(killer);

        var registry = new InMemoryLocalRuntimeRegistry();
        var service = NewService(locator, api, launcher, registry, DefaultStore());

        await Should.ThrowAsync<ProviderStopFailedException>(
            () => service.StopAsync(CancellationToken.None));
    }
}
