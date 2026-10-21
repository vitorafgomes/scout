using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Scout.Core.Plugins;
using Scout.Core.Features;
using Scout.Plugin.GitHub.Functions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// Scout feature plugin that exposes a thin, audited surface over the
/// GitHub REST API via Octokit. Implements both the static
/// <see cref="IScoutPlugin"/> contract (so it can register through
/// <c>PluginRegistry</c>) and the dynamic <see cref="IFeaturePlugin"/>
/// contract added in Phase 1 (so the runtime feature registrar can
/// attach it on demand once the user provides a PAT).
/// </summary>
public sealed class GitHubPlugin : IScoutPlugin, IFeaturePlugin
{
    /// <summary>Stable Semantic Kernel plugin name.</summary>
    public const string PluginName = "GitHub";

    /// <summary>Catalog feature id this plugin serves.</summary>
    public const string FeatureIdValue = "github";

    private readonly IServiceProvider _services;

    /// <summary>
    /// DI constructor. Holds the service provider so
    /// <see cref="BuildAsync"/> can resolve <see cref="GitHubFunctions"/>
    /// without an extra parameter — matches the existing dynamic
    /// registration shape.
    /// </summary>
    public GitHubPlugin(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc/>
    public string Name => PluginName;

    /// <inheritdoc/>
    public PluginCapabilities Capabilities => PluginCapabilities.ReadOnly;

    /// <inheritdoc/>
    public string FeatureId => FeatureIdValue;

    /// <inheritdoc/>
    public string KernelPluginName => PluginName;

    /// <inheritdoc/>
    public KernelPlugin Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var functions = services.GetRequiredService<GitHubFunctions>();
        return KernelPluginFactory.CreateFromObject(functions, PluginName);
    }

    /// <inheritdoc/>
    public Task<KernelPlugin> BuildAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Build(_services));
    }
}
