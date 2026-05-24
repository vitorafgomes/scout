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

public sealed class FeatureKernelRegistrarTests
{
    [Fact]
    public async Task RegisterAsync_NoFactory_IsNoOp()
    {
        var (host, _) = BuildHost();
        var registry = new FeaturePluginFactoryRegistry(
            Array.Empty<FeaturePluginFactoryRegistration>(),
            new ServiceCollection().BuildServiceProvider());

        using var sut = new FeatureKernelRegistrar(
            host,
            registry,
            NullLogger<FeatureKernelRegistrar>.Instance);

        await sut.RegisterAsync("github", CancellationToken.None);
        sut.IsRegistered("github").ShouldBeFalse();
    }

    [Fact]
    public async Task RegisterAsync_AddsPluginToKernel_OnlyOnce()
    {
        var (host, _) = BuildHost();
        var plugin = new StubPlugin("github", "GitHub");
        var registry = BuildRegistry(plugin);

        using var sut = new FeatureKernelRegistrar(
            host,
            registry,
            NullLogger<FeatureKernelRegistrar>.Instance);

        await sut.RegisterAsync("github", CancellationToken.None);
        await sut.RegisterAsync("github", CancellationToken.None);

        sut.IsRegistered("github").ShouldBeTrue();
        host.ResolvedKernel.Plugins.Count(p => p.Name == "GitHub").ShouldBe(1);
        plugin.BuildCount.ShouldBe(1);
    }

    [Fact]
    public async Task UnregisterAsync_RemovesPluginFromKernel()
    {
        var (host, _) = BuildHost();
        var plugin = new StubPlugin("github", "GitHub");
        var registry = BuildRegistry(plugin);

        using var sut = new FeatureKernelRegistrar(
            host,
            registry,
            NullLogger<FeatureKernelRegistrar>.Instance);

        await sut.RegisterAsync("github", CancellationToken.None);
        await sut.UnregisterAsync("github", CancellationToken.None);

        sut.IsRegistered("github").ShouldBeFalse();
        host.ResolvedKernel.Plugins.Any(p => p.Name == "GitHub").ShouldBeFalse();
    }

    [Fact]
    public async Task UnregisterAsync_Unknown_IsNoOp()
    {
        var (host, _) = BuildHost();
        var registry = new FeaturePluginFactoryRegistry(
            Array.Empty<FeaturePluginFactoryRegistration>(),
            new ServiceCollection().BuildServiceProvider());

        using var sut = new FeatureKernelRegistrar(
            host,
            registry,
            NullLogger<FeatureKernelRegistrar>.Instance);

        await sut.UnregisterAsync("github", CancellationToken.None);
        sut.IsRegistered("github").ShouldBeFalse();
    }

    [Fact]
    public async Task RegisterAsync_FactoryThrows_WrapsAsRegistrationException()
    {
        var (host, _) = BuildHost();
        var plugin = new ThrowingPlugin("github");
        var registry = BuildRegistry(plugin);

        using var sut = new FeatureKernelRegistrar(
            host,
            registry,
            NullLogger<FeatureKernelRegistrar>.Instance);

        await Should.ThrowAsync<FeatureRegistrationException>(() =>
            sut.RegisterAsync("github", CancellationToken.None));

        sut.IsRegistered("github").ShouldBeFalse();
    }

    [Fact]
    public async Task ConcurrentRegisterUnregister_DoesNotCorruptKernel()
    {
        var (host, _) = BuildHost();
        var plugin = new StubPlugin("github", "GitHub");
        var registry = BuildRegistry(plugin);

        using var sut = new FeatureKernelRegistrar(
            host,
            registry,
            NullLogger<FeatureKernelRegistrar>.Instance);

        var tasks = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            tasks.Add(sut.RegisterAsync("github", CancellationToken.None));
            tasks.Add(sut.UnregisterAsync("github", CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        // Final state can be either registered or not, but plugin count
        // for "GitHub" must be 0 or 1, never higher.
        host.ResolvedKernel.Plugins.Count(p => p.Name == "GitHub").ShouldBeLessThanOrEqualTo(1);
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
        public int BuildCount { get; private set; }

        public Task<KernelPlugin> BuildAsync(CancellationToken cancellationToken)
        {
            BuildCount++;
            return Task.FromResult(KernelPluginFactory.CreateFromFunctions(_pluginName));
        }
    }

    private sealed class ThrowingPlugin : IFeaturePlugin
    {
        public ThrowingPlugin(string featureId) => FeatureId = featureId;
        public string FeatureId { get; }
        public string KernelPluginName => "Throwing";
        public Task<KernelPlugin> BuildAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class FakeLlm : ILlmProviderFactory
    {
        public string Name => "Fake";

        public void AddToKernel(IKernelBuilder builder)
        {
            // no-op — unit tests only exercise plugin attachment.
        }
    }
}
