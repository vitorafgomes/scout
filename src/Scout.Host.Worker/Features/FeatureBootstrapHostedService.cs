using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scout.Host.Worker.Features;

/// <summary>
/// Runs after <c>AddLocalProviders</c> so Ollama autostart finishes
/// first. Reads persisted feature state and re-attaches enabled features
/// whose credentials are still present. A bad factory or registrar
/// failure marks the feature as <c>error</c> but never blocks startup.
/// </summary>
public sealed class FeatureBootstrapHostedService : IHostedService
{
    private readonly IFeatureCatalog _catalog;
    private readonly IFeatureStateStore _stateStore;
    private readonly ICredentialStore _credentials;
    private readonly IFeatureKernelRegistrar _registrar;
    private readonly FeaturePluginFactoryRegistry _factories;
    private readonly ILogger<FeatureBootstrapHostedService> _logger;

    /// <summary>Build the bootstrap service.</summary>
    public FeatureBootstrapHostedService(
        IFeatureCatalog catalog,
        IFeatureStateStore stateStore,
        ICredentialStore credentials,
        IFeatureKernelRegistrar registrar,
        FeaturePluginFactoryRegistry factories,
        ILogger<FeatureBootstrapHostedService> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, FeatureState> persisted;
        try
        {
            persisted = await _stateStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Feature bootstrap could not read state; skipping");
            return;
        }

        foreach (var metadata in _catalog.All)
        {
            if (!persisted.TryGetValue(metadata.Id, out var state) || !state.Enabled)
            {
                continue;
            }

            if (!_factories.HasFactory(metadata.Id))
            {
                _logger.LogInformation(
                    "Feature {FeatureId} is enabled but has no factory yet; skipping",
                    metadata.Id);
                continue;
            }

            if (metadata.RequiresCredentials)
            {
                var hasAll = await HasAllCredentialsAsync(metadata, cancellationToken)
                    .ConfigureAwait(false);
                if (!hasAll)
                {
                    _logger.LogInformation(
                        "Feature {FeatureId} is enabled but credentials are missing; skipping",
                        metadata.Id);
                    continue;
                }
            }

            try
            {
                await _registrar.RegisterAsync(metadata.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Feature {FeatureId} failed to register on bootstrap; marking as error",
                    metadata.Id);
                try
                {
                    await _stateStore.SetAsync(
                        metadata.Id,
                        new FeatureState(Enabled: true, LastError: ex.Message),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception persistEx)
                {
                    _logger.LogWarning(
                        persistEx,
                        "Could not persist error state for feature {FeatureId}",
                        metadata.Id);
                }
            }
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<bool> HasAllCredentialsAsync(FeatureMetadata metadata, CancellationToken ct)
    {
        foreach (var field in metadata.CredentialFields.Where(IsRequired))
        {
            var key = $"scout/{metadata.Id}/{field.Key}";
            var present = await _credentials.ContainsAsync(key, ct).ConfigureAwait(false);
            if (!present)
            {
                return false;
            }
        }

        return true;
    }

    // Mirrors the convention in FeaturesEndpoints: "text" inputs are
    // optional, everything else gates the feature.
    private static bool IsRequired(FeatureCredentialFieldDto field) =>
        !string.Equals(field.Kind, "text", StringComparison.Ordinal);
}
