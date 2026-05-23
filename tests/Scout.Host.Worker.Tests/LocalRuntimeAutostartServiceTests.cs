using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class LocalRuntimeAutostartServiceTests
{
    private const string OllamaPath = "/usr/local/bin/ollama";

    private static IOptions<OllamaProviderOptions> Opts(TimeSpan? warmupBudget = null) =>
        Options.Create(new OllamaProviderOptions
        {
            Endpoint = "http://localhost:11434",
            PingTimeout = TimeSpan.FromMilliseconds(200),
            StartTimeout = TimeSpan.FromSeconds(2),
            WarmupBudget = warmupBudget ?? TimeSpan.FromSeconds(5),
        });

    private static OllamaProviderService NewProvider(
        IBinaryLocator locator,
        IOllamaApiClient api,
        IProcessLauncher launcher,
        ILocalRuntimeRegistry registry,
        ILocalRuntimeConfigStore store,
        OllamaReadinessProbe probe,
        IOptions<OllamaProviderOptions>? options = null) =>
        new(
            locator,
            api,
            launcher,
            registry,
            store,
            options ?? Opts(),
            probe,
            NullLogger<OllamaProviderService>.Instance);

    [Fact]
    public async Task StartingAsync_OllamaAutostartDisabled_MarksProbeFailed()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));
        var launcher = Substitute.For<IProcessLauncher>();
        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        store.GetAutostartAsync("lm-studio", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var probe = new OllamaReadinessProbe();
        var options = Opts();
        var provider = NewProvider(locator, api, launcher, registry, store, probe, options);
        var sut = new LocalRuntimeAutostartService(
            provider, store, probe, options, NullLogger<LocalRuntimeAutostartService>.Instance);

        await sut.StartingAsync(CancellationToken.None);

        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
        probe.Current.ShouldBe(OllamaReadinessState.Failed);
    }

    [Fact]
    public async Task StartingAsync_OllamaAlreadyRunning_MarksProbeReady()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>())));
        var launcher = Substitute.For<IProcessLauncher>();
        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var probe = new OllamaReadinessProbe();
        var options = Opts();
        var provider = NewProvider(locator, api, launcher, registry, store, probe, options);
        var sut = new LocalRuntimeAutostartService(
            provider, store, probe, options, NullLogger<LocalRuntimeAutostartService>.Instance);

        await sut.StartingAsync(CancellationToken.None);

        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
        probe.Current.ShouldBe(OllamaReadinessState.Ready);
    }

    [Fact]
    public async Task StartingAsync_OllamaSpawnsAndBecomesReachable_MarksProbeReady()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));

        var apiCalls = 0;
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            apiCalls++;
            // First three pings unreachable (autostart status pre-flight +
            // StartCoreAsync pre-gate + in-gate); later pings (post-spawn
            // readiness loop) return reachable so readiness completes.
            return Task.FromResult<OllamaTags?>(apiCalls <= 3
                ? null
                : new OllamaTags(Array.Empty<ProviderModel>()));
        });

        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.HasExited.Returns(false);
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fakeProc);

        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var probe = new OllamaReadinessProbe();
        var options = Opts();
        var provider = NewProvider(locator, api, launcher, registry, store, probe, options);
        var sut = new LocalRuntimeAutostartService(
            provider, store, probe, options, NullLogger<LocalRuntimeAutostartService>.Instance);

        await sut.StartingAsync(CancellationToken.None);

        launcher.Received(1).Launch(Arg.Any<ProcessLaunchRequest>());
        probe.Current.ShouldBe(OllamaReadinessState.Ready);
    }

    [Fact]
    public async Task StartingAsync_OllamaNeverComesUp_MarksProbeFailed()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));

        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.HasExited.Returns(false);
        fakeProc.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fakeProc);

        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var probe = new OllamaReadinessProbe();
        var options = Opts(warmupBudget: TimeSpan.FromMilliseconds(300));
        var provider = NewProvider(locator, api, launcher, registry, store, probe, options);
        var sut = new LocalRuntimeAutostartService(
            provider, store, probe, options, NullLogger<LocalRuntimeAutostartService>.Instance);

        await Should.NotThrowAsync(() => sut.StartingAsync(CancellationToken.None));

        probe.Current.ShouldBe(OllamaReadinessState.Failed);
    }

    [Fact]
    public async Task StartingAsync_AutostartDisabledAndDaemonAbsent_MarksProbeFailed_HostStillStarts()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));
        var launcher = Substitute.For<IProcessLauncher>();
        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var probe = new OllamaReadinessProbe();
        var options = Opts();
        var provider = NewProvider(locator, api, launcher, registry, store, probe, options);
        var sut = new LocalRuntimeAutostartService(
            provider, store, probe, options, NullLogger<LocalRuntimeAutostartService>.Instance);

        await Should.NotThrowAsync(() => sut.StartingAsync(CancellationToken.None));

        probe.Current.ShouldBe(OllamaReadinessState.Failed);
        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
    }

    [Fact]
    public async Task StartingAsync_BinaryNotInstalled_FastFailsProbe_WithoutAnyHttpPing()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var api = Substitute.For<IOllamaApiClient>();
        var launcher = Substitute.For<IProcessLauncher>();
        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var probe = new OllamaReadinessProbe();
        var options = Opts();
        var provider = NewProvider(locator, api, launcher, registry, store, probe, options);
        var sut = new LocalRuntimeAutostartService(
            provider, store, probe, options, NullLogger<LocalRuntimeAutostartService>.Instance);

        await sut.StartingAsync(CancellationToken.None);

        probe.Current.ShouldBe(OllamaReadinessState.Failed);
        await api.DidNotReceive().GetTagsAsync(Arg.Any<CancellationToken>());
        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
    }

    [Fact]
    public async Task StartAsync_UserInitiated_AfterAutostartFailed_RebootsProbeAndSucceeds()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));

        // Autostart phase: every ping returns null so warmup fails.
        // User phase: pings start returning reachable so the spawn succeeds.
        var autostartDone = false;
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult<OllamaTags?>(
                autostartDone ? new OllamaTags(Array.Empty<ProviderModel>()) : null));

        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.HasExited.Returns(false);
        fakeProc.StopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(fakeProc);

        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var probe = new OllamaReadinessProbe();
        var options = Opts(warmupBudget: TimeSpan.FromMilliseconds(300));
        var provider = NewProvider(locator, api, launcher, registry, store, probe, options);
        var sut = new LocalRuntimeAutostartService(
            provider, store, probe, options, NullLogger<LocalRuntimeAutostartService>.Instance);

        await sut.StartingAsync(CancellationToken.None);
        probe.Current.ShouldBe(OllamaReadinessState.Failed);

        // User clicks Start: probe must be reset and the spawn must succeed.
        autostartDone = true;
        var status = await provider.StartAsync(CancellationToken.None);

        status.Running.ShouldBeTrue();
        probe.Current.ShouldBe(OllamaReadinessState.Ready);
    }

    [Fact]
    public async Task StartingAsync_OllamaStartFails_HostStillStarts()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));

        var launcher = Substitute.For<IProcessLauncher>();
        // Launch throws synchronously: the StartingAsync path must swallow it
        // so the hosted lifecycle Start does not propagate.
        launcher.Launch(Arg.Any<ProcessLaunchRequest>())
            .Returns(_ => throw new InvalidOperationException("boom"));

        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var probe = new OllamaReadinessProbe();
        var options = Opts();
        var provider = NewProvider(locator, api, launcher, registry, store, probe, options);
        var sut = new LocalRuntimeAutostartService(
            provider, store, probe, options, NullLogger<LocalRuntimeAutostartService>.Instance);

        await Should.NotThrowAsync(() => sut.StartingAsync(CancellationToken.None));

        probe.Current.ShouldBe(OllamaReadinessState.Failed);
    }
}
