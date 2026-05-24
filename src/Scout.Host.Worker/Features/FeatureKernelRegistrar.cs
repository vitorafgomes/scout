using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Scout.Core.Hosting;

namespace Scout.Host.Worker.Features;

/// <summary>
/// Default <see cref="IFeatureKernelRegistrar"/>. Serialises mutations
/// of <see cref="KernelPluginCollection"/> through a semaphore because
/// the collection is not thread-safe.
/// </summary>
public sealed class FeatureKernelRegistrar : IFeatureKernelRegistrar, IDisposable
{
    private readonly ScoutHost _host;
    private readonly FeaturePluginFactoryRegistry _factories;
    private readonly ILogger<FeatureKernelRegistrar> _logger;
    private readonly ConcurrentDictionary<string, string> _registered = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Build the registrar.</summary>
    public FeatureKernelRegistrar(
        ScoutHost host,
        FeaturePluginFactoryRegistry factories,
        ILogger<FeatureKernelRegistrar> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public bool IsRegistered(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        return _registered.ContainsKey(featureId);
    }

    /// <inheritdoc/>
    public async Task RegisterAsync(string featureId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);

        var plugin = _factories.Resolve(featureId);
        if (plugin is null)
        {
            // No factory registered yet (Phase 1 state). Callers translate
            // this to 501; we just refuse silently here.
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_registered.ContainsKey(featureId))
            {
                return;
            }

            KernelPlugin built;
            try
            {
                built = await plugin.BuildAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new FeatureRegistrationException(
                    featureId,
                    $"Feature plugin '{featureId}' failed to build its kernel plugin.",
                    ex);
            }

            var kernel = _host.ResolvedKernel;
            kernel.Plugins.Add(built);
            _registered[featureId] = built.Name;

            _logger.LogInformation(
                "Feature {FeatureId} attached kernel plugin {PluginName}",
                featureId,
                built.Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task UnregisterAsync(string featureId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_registered.TryRemove(featureId, out var pluginName))
            {
                return;
            }

            var kernel = _host.ResolvedKernel;
            var existing = kernel.Plugins.FirstOrDefault(p =>
                string.Equals(p.Name, pluginName, StringComparison.Ordinal));
            if (existing is not null)
            {
                kernel.Plugins.Remove(existing);
            }

            _logger.LogInformation(
                "Feature {FeatureId} detached kernel plugin {PluginName}",
                featureId,
                pluginName);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();
}
