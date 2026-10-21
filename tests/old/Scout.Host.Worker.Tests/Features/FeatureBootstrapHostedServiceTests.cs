using Scout.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using NSubstitute;
using Scout.Core.Chat;
using Scout.Core.Hosting;
using Scout.Core.Llm;
using Scout.Core.Plugins;
using Scout.Host.Worker.Features;

namespace Scout.Host.Worker.Tests.Features;

public sealed class FeatureBootstrapHostedServiceTests
{
    [Fact]
    public async Task StartAsync_RegistersEnabledFeaturesWithCredentials()
    {
        var catalog = new FeatureCatalog();
        var stateStore = new InMemoryFeatureStateStore();
        await stateStore.SetAsync("github", FeatureStates.EnabledWithoutSettings(), CancellationToken.None);

        var credentials = new InMemoryCredentialStore(isAvailable: true);
        await credentials.StoreAsync("scout/github/pat", "x", CancellationToken.None);

        var plugin = new StubPlugin("github", "GitHub");
        var registry = BuildRegistry(plugin);
        var (host, _) = BuildHost();
        var registrar = new FeatureKernelRegistrar(
            host,
            registry,
            NullLogger<FeatureKernelRegistrar>.Instance);

        var sut = new FeatureBootstrapHostedService(
            catalog,
            stateStore,
            credentials,
            registrar,
            registry,
            NullLogger<FeatureBootstrapHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        registrar.IsRegistered("github").ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_SkipsEnabledWithoutCredentials()
    {
        var catalog = new FeatureCatalog();
        var stateStore = new InMemoryFeatureStateStore();
        await stateStore.SetAsync("github", FeatureStates.EnabledWithoutSettings(), CancellationToken.None);

        var credentials = new InMemoryCredentialStore(isAvailable: true);
        var plugin = new StubPlugin("github", "GitHub");
        var registry = BuildRegistry(plugin);
        var (host, _) = BuildHost();
        var registrar = new FeatureKernelRegistrar(
            host,
            registry,
            NullLogger<FeatureKernelRegistrar>.Instance);

        var sut = new FeatureBootstrapHostedService(
            catalog,
            stateStore,
            credentials,
            registrar,
            registry,
            NullLogger<FeatureBootstrapHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        registrar.IsRegistered("github").ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_SkipsEnabledWithoutFactory()
    {
        var catalog = new FeatureCatalog();
        var stateStore = new InMemoryFeatureStateStore();
        await stateStore.SetAsync("github", FeatureStates.EnabledWithoutSettings(), CancellationToken.None);

        var credentials = new InMemoryCredentialStore(isAvailable: true);
        await credentials.StoreAsync("scout/github/pat", "x", CancellationToken.None);

        var registry = new FeaturePluginFactoryRegistry(
            Array.Empty<FeaturePluginFactoryRegistration>(),
            new ServiceCollection().BuildServiceProvider());

        var registrar = Substitute.For<IFeatureKernelRegistrar>();

        var sut = new FeatureBootstrapHostedService(
            catalog,
            stateStore,
            credentials,
            registrar,
            registry,
            NullLogger<FeatureBootstrapHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        await registrar.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default);
    }

    [Fact]
    public async Task StartAsync_BadFeature_DoesNotBlockOthers()
    {
        var catalog = new FeatureCatalog();
        var stateStore = new InMemoryFeatureStateStore();
        await stateStore.SetAsync("github", FeatureStates.EnabledWithoutSettings(), CancellationToken.None);
        await stateStore.SetAsync("kubernetes", FeatureStates.EnabledWithoutSettings(), CancellationToken.None);

        var credentials = new InMemoryCredentialStore(isAvailable: true);
        await credentials.StoreAsync("scout/github/pat", "x", CancellationToken.None);
        await credentials.StoreAsync("scout/kubernetes/kubeconfig", "k", CancellationToken.None);

        var registrar = Substitute.For<IFeatureKernelRegistrar>();
        registrar
            .RegisterAsync("github", Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("github boom"));

        var registry = new FeaturePluginFactoryRegistry(
            new[]
            {
                new FeaturePluginFactoryRegistration("github", typeof(StubPlugin)),
                new FeaturePluginFactoryRegistration("kubernetes", typeof(StubPlugin)),
            },
            BuildPluginProvider());

        var sut = new FeatureBootstrapHostedService(
            catalog,
            stateStore,
            credentials,
            registrar,
            registry,
            NullLogger<FeatureBootstrapHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        await registrar.Received().RegisterAsync("kubernetes", Arg.Any<CancellationToken>());
        var ghState = await stateStore.GetAsync("github", CancellationToken.None);
        ghState.LastError.ShouldNotBeNull();
    }

    private static IServiceProvider BuildPluginProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new StubPlugin("github", "GitHub"));
        return services.BuildServiceProvider();
    }

    private static FeaturePluginFactoryRegistry BuildRegistry(IFeaturePlugin plugin)
    {
        var services = new ServiceCollection();
        services.AddSingleton(plugin.GetType(), plugin);
        var provider = services.BuildServiceProvider();
        return new FeaturePluginFactoryRegistry(
            new[] { new FeaturePluginFactoryRegistration(plugin.FeatureId, plugin.GetType()) },
            provider);
    }

    private static (ScoutHost Host, IServiceProvider Services) BuildHost()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var host = new ScoutHost(
            new PluginRegistry(),
            new FakeLlm(),
            services,
            NullLogger<ScoutHost>.Instance,
            Options.Create(new ScoutOptions()),
            Array.Empty<IMessageRouter>());
        return (host, services);
    }

    private sealed class StubPlugin : IFeaturePlugin
    {
        private readonly string _pluginName;
        public StubPlugin(string featureId, string pluginName)
        {
            FeatureId = featureId;
            _pluginName = pluginName;
        }

        public string FeatureId { get; }
        public string KernelPluginName => _pluginName;
        public Task<KernelPlugin> BuildAsync(CancellationToken cancellationToken) =>
            Task.FromResult(KernelPluginFactory.CreateFromFunctions(_pluginName));
    }

    private sealed class InMemoryFeatureStateStore : IFeatureStateStore
    {
        private readonly Dictionary<string, FeatureState> _state = new(StringComparer.Ordinal);

        public Task<FeatureState> GetAsync(string featureId, CancellationToken ct)
        {
            return Task.FromResult(_state.TryGetValue(featureId, out var v)
                ? v
                : FeatureStates.EmptyDisabled());
        }

        public Task SetAsync(string featureId, FeatureState state, CancellationToken ct)
        {
            _state[featureId] = state;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, FeatureState>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, FeatureState>>(
                new Dictionary<string, FeatureState>(_state, StringComparer.Ordinal));
    }

    private sealed class FakeLlm : ILlmProviderFactory
    {
        public string Name => "Fake";

        public void AddToKernel(IKernelBuilder builder)
        {
        }
    }
}
