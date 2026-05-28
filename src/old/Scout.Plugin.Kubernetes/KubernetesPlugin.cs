using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Core.Plugins;
using Scout.Core.Features;

namespace Scout.Plugin.Kubernetes;

/// <summary>
/// Read-only Kubernetes plugin. Implements both <see cref="IScoutPlugin"/>
/// for the host's plugin registry and <see cref="IFeaturePlugin"/> so the
/// feature subsystem can attach / detach it as the user toggles the
/// Kubernetes feature in the UI.
/// </summary>
public sealed class KubernetesPlugin : IScoutPlugin, IFeaturePlugin
{
    /// <summary>Stable plugin name surfaced through the kernel.</summary>
    public const string PluginName = "Kubernetes";

    /// <summary>Feature id this plugin implements.</summary>
    public const string Id = "kubernetes";

    private readonly IServiceProvider _services;

    /// <summary>Parameterless ctor used by tests that only check metadata.</summary>
    public KubernetesPlugin()
        : this(EmptyServiceProvider.Instance)
    {
    }

    /// <summary>DI ctor — receives the host service provider so it can resolve <see cref="KubernetesFunctions"/>.</summary>
    public KubernetesPlugin(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc/>
    public string Name => PluginName;

    /// <inheritdoc/>
    public string FeatureId => Id;

    /// <inheritdoc/>
    public string KernelPluginName => PluginName;

    /// <inheritdoc/>
    public PluginCapabilities Capabilities => PluginCapabilities.ReadOnly;

    /// <inheritdoc/>
    public KernelPlugin Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var functions = services.GetRequiredService<KubernetesFunctions>();
        return KernelPluginFactory.CreateFromObject(functions, PluginName);
    }

    /// <inheritdoc/>
    public Task<KernelPlugin> BuildAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Build(_services));
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
