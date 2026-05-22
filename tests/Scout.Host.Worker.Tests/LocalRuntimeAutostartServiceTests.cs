using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class LocalRuntimeAutostartServiceTests
{
    private const string OllamaPath = "/usr/local/bin/ollama";

    private static IOptions<OllamaProviderOptions> Opts() =>
        Options.Create(new OllamaProviderOptions
        {
            Endpoint = "http://localhost:11434",
            PingTimeout = TimeSpan.FromMilliseconds(200),
            StartTimeout = TimeSpan.FromSeconds(2),
        });

    private static OllamaProviderService NewProvider(
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

    [Fact]
    public async Task StartAsync_OllamaAutostartDisabled_DoesNotStart()
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

        var provider = NewProvider(locator, api, launcher, registry, store);
        var sut = new LocalRuntimeAutostartService(
            provider, store, NullLogger<LocalRuntimeAutostartService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        // Give any (incorrect) fire-and-forget tasks a moment to surface.
        await Task.Delay(50);

        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
    }

    [Fact]
    public async Task StartAsync_OllamaAutostartEnabledButAlreadyRunning_DoesNotStart()
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

        var provider = NewProvider(locator, api, launcher, registry, store);
        var sut = new LocalRuntimeAutostartService(
            provider, store, NullLogger<LocalRuntimeAutostartService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        launcher.DidNotReceiveWithAnyArgs().Launch(default!);
    }

    [Fact]
    public async Task StartAsync_OllamaAutostartEnabledAndStopped_FiresStart()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));

        var callCount = 0;
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            callCount++;
            // First call (status pre-flight by autostart) is unreachable.
            // Subsequent calls from inside StartAsync return reachable so
            // the spawned daemon completes its readiness loop.
            return Task.FromResult<OllamaTags?>(callCount <= 3
                ? null
                : new OllamaTags(Array.Empty<ProviderModel>()));
        });

        var launchedSignal = new TaskCompletionSource();
        var launcher = Substitute.For<IProcessLauncher>();
        var fakeProc = Substitute.For<ILaunchedProcess>();
        fakeProc.HasExited.Returns(false);
        launcher.Launch(Arg.Any<ProcessLaunchRequest>()).Returns(_ =>
        {
            launchedSignal.TrySetResult();
            return fakeProc;
        });

        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var provider = NewProvider(locator, api, launcher, registry, store);
        var sut = new LocalRuntimeAutostartService(
            provider, store, NullLogger<LocalRuntimeAutostartService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        var completed = await Task.WhenAny(launchedSignal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBe(launchedSignal.Task);
        launcher.Received(1).Launch(Arg.Any<ProcessLaunchRequest>());
    }

    [Fact]
    public async Task StartAsync_OllamaStartFails_HostStillStarts()
    {
        var locator = Substitute.For<IBinaryLocator>();
        locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(OllamaPath));
        var api = Substitute.For<IOllamaApiClient>();
        api.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OllamaTags?>(null));

        var launcher = Substitute.For<IProcessLauncher>();
        // Launch throws synchronously: the fire-and-forget Task.Run must
        // swallow it so the hosted service Start does not propagate.
        launcher.Launch(Arg.Any<ProcessLaunchRequest>())
            .Returns(_ => throw new InvalidOperationException("boom"));

        var registry = new InMemoryLocalRuntimeRegistry();
        var store = Substitute.For<ILocalRuntimeConfigStore>();
        store.GetAutostartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var provider = NewProvider(locator, api, launcher, registry, store);
        var sut = new LocalRuntimeAutostartService(
            provider, store, NullLogger<LocalRuntimeAutostartService>.Instance);

        // Must not throw.
        await sut.StartAsync(CancellationToken.None);
        // Allow the fire-and-forget task to run.
        await Task.Delay(100);
    }
}
