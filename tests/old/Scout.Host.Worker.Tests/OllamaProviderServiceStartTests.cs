using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Scout.Host.Worker.Tests.Helpers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OllamaProviderServiceStartTests
{
    private const string OllamaPath = "/usr/local/bin/ollama";

    private static IOptions<OllamaProviderOptions> Opts(TimeSpan? startTimeout = null) =>
        Options.Create(new OllamaProviderOptions
        {
            Endpoint = "http://localhost:11434",
            PingTimeout = TimeSpan.FromMilliseconds(200),
            StartTimeout = startTimeout ?? TimeSpan.FromSeconds(2),
        });

    private static (IBinaryLocator Locator, IOllamaApiClient Api, ILocalRuntimeConfigStore Store) BaseDeps(
        bool installed = true)
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(installed ? OllamaPath : null));
        var api = Substitute.For<IOllamaApiClient>();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        return (locator, api, store);
    }

    private static OllamaProviderService NewService(
        IBinaryLocator locator,
        IOllamaApiClient api,
        IProcessLauncher launcher,
        ILocalRuntimeRegistry registry,
        ILocalRuntimeConfigStore store,
        IOptions<OllamaProviderOptions>? options = null,
        IOllamaReadinessProbe? probe = null) =>
        new(
            locator,
            api,
            launcher,
            registry,
            store,
            options ?? Opts(),
            probe ?? FakeReadinessProbes.AlwaysReady(),
            NullLogger<OllamaProviderService>.Instance);

    [Fact]
    public async Task StartAsync_AlreadyRunning_IsIdempotentNoSpawn()
    {
        var (locator, api, store) = BaseDeps();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>())));
        var launcher = Substitute.For<IProcessLauncher>();
        var registry = new InMemoryLocalRuntimeRegistry();

        var service = NewService(locator, api, launcher, registry, store);

        var status = await service.StartAsync(CancellationToken.None);

        status.Running.ShouldBeTrue();
        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
    }

    [Fact]
    public async Task StartAsync_BinaryAbsent_ReturnsNotInstalledStatus()
    {
        var (locator, api, store) = BaseDeps(installed: false);
        var launcher = Substitute.For<IProcessLauncher>();
        var registry = new InMemoryLocalRuntimeRegistry();

        var service = NewService(locator, api, launcher, registry, store);

        var status = await service.StartAsync(CancellationToken.None);

        status.Installed.ShouldBeFalse();
        status.Running.ShouldBeFalse();
        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
    }

    [Fact]
    public async Task StartAsync_BecomesReachable_ReturnsRunningAndTracksPid()
    {
        var (locator, api, store) = BaseDeps();

        // First call (status pre-start) -> null, second call (poll #1) -> tags
        var queue = new Queue<OllamaTags?>(new OllamaTags?[]
        {
            null, // initial GetStatusAsync (pre-gate)
            null, // GetStatusAsync inside the gate (re-check)
            new OllamaTags(Array.Empty<ProviderModel>()), // first readiness poll
            new OllamaTags(Array.Empty<ProviderModel>()), // final GetStatusAsync
        });
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(queue.Count > 0 ? queue.Dequeue() : null));

        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.Pid.Returns(12345);
        fakeProc.HasExited.Returns(false);
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fakeProc);

        var registry = new InMemoryLocalRuntimeRegistry();

        var service = NewService(locator, api, launcher, registry, store);

        var status = await service.StartAsync(CancellationToken.None);

        status.Running.ShouldBeTrue();
        status.ManagedByWorker.ShouldBeTrue();
        registry.GetOwned("ollama").ShouldBeSameAs(fakeProc);
        launcher.Received(1).Launch(Arg.Is<ProcessLaunchRequest>(r =>
            r.FileName == OllamaPath
            && r.Arguments.Count == 1
            && r.Arguments[0] == "serve"));
    }

    [Fact]
    public async Task StartAsync_NeverBecomesReachable_KillsChildAndThrowsTimeout()
    {
        var (locator, api, store) = BaseDeps();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(null));

        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.Pid.Returns(8888);
        fakeProc.HasExited.Returns(false);
        fakeProc.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fakeProc);

        var registry = new InMemoryLocalRuntimeRegistry();

        var service = NewService(
            locator,
            api,
            launcher,
            registry,
            store,
            Opts(TimeSpan.FromMilliseconds(300)));

        await Should.ThrowAsync<ProviderStartTimeoutException>(
            () => service.StartAsync(CancellationToken.None));

        await fakeProc.Received().StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        registry.GetOwned("ollama").ShouldBeNull();
    }

    [Fact]
    public async Task StartAsync_DaemonExitsMidPoll_ThrowsStartFailed()
    {
        var (locator, api, store) = BaseDeps();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(null));

        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.Pid.Returns(9999);
        // First HasExited check (right before first ping) reports running,
        // every subsequent check reports exited so the loop trips on the
        // exit branch instead of the timeout branch.
        var exitCalls = 0;
        fakeProc.HasExited.Returns(_ =>
        {
            exitCalls++;
            return exitCalls > 1;
        });
        fakeProc.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fakeProc);

        var registry = new InMemoryLocalRuntimeRegistry();

        var service = NewService(
            locator,
            api,
            launcher,
            registry,
            store,
            Opts(TimeSpan.FromSeconds(2)));

        await Should.ThrowAsync<ProviderStartFailedException>(
            () => service.StartAsync(CancellationToken.None));

        registry.GetOwned("ollama").ShouldBeNull();
    }

    [Fact]
    public async Task StartAsync_ConcurrentCalls_OnlyOneSpawn()
    {
        var (locator, api, store) = BaseDeps();

        // Simulate a slow boot: status before the gate reports unreachable
        // for both concurrent callers. The first to enter the gate spawns,
        // polls succeed, and the second caller, on its in-gate re-check,
        // sees a running daemon and short-circuits.
        var spawned = false;
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<OllamaTags?>(
                spawned ? new OllamaTags(Array.Empty<ProviderModel>()) : null));

        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.HasExited.Returns(false);
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(_ =>
        {
            spawned = true;
            return fakeProc;
        });

        var registry = new InMemoryLocalRuntimeRegistry();
        var service = NewService(locator, api, launcher, registry, store);

        var t1 = service.StartAsync(CancellationToken.None);
        var t2 = service.StartAsync(CancellationToken.None);
        await Task.WhenAll(t1, t2);

        launcher.Received(1).Launch(Arg.Any<ProcessLaunchRequest>());
    }
}
