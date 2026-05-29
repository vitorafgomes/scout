using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Scout.ControlPlane.Providers;

namespace Scout.ControlPlane.Tests.Providers;

/// <summary>
/// Builds an <see cref="OllamaProviderService"/> with substituted dependencies
/// so provider-domain behaviour can be exercised without a real daemon.
/// </summary>
internal sealed class OllamaTestHarness
{
    public IBinaryLocator Locator { get; } = Substitute.For<IBinaryLocator>();
    public IOllamaApiClient Api { get; } = Substitute.For<IOllamaApiClient>();
    public IProcessLauncher Launcher { get; } = Substitute.For<IProcessLauncher>();
    public ILocalRuntimeRegistry Registry { get; } = Substitute.For<ILocalRuntimeRegistry>();
    public ILocalRuntimeConfigStore ConfigStore { get; } = Substitute.For<ILocalRuntimeConfigStore>();
    public IOllamaReadinessProbe Probe { get; } = Substitute.For<IOllamaReadinessProbe>();
    public OllamaProviderOptions Options { get; } = new();

    public OllamaTestHarness()
    {
        // Sensible hermetic defaults: ready, binary present, daemon up, autostart on.
        Probe.WaitReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        Locator.FindOnPathAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("/usr/bin/ollama"));
        Api.GetTagsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OllamaTags?>(new OllamaTags(Array.Empty<ProviderModel>())));
        ConfigStore.GetAutostartAsync("ollama", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        Registry.GetOwned("ollama").Returns((ILaunchedProcess?)null);
    }

    public OllamaProviderService Build() => new(
        Locator,
        Api,
        Launcher,
        Registry,
        ConfigStore,
        OptionsWrapper(),
        Probe,
        NullLogger<OllamaProviderService>.Instance);

    public ProvidersService BuildProviders() => new(Build());

    private IOptions<OllamaProviderOptions> OptionsWrapper() =>
        Microsoft.Extensions.Options.Options.Create(Options);
}
