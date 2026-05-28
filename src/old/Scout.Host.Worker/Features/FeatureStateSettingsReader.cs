using Scout.Core.Features;

namespace Scout.Host.Worker.Features;

/// <summary>
/// <see cref="IFeatureSettingsReader"/> backed by the worker's
/// <see cref="IFeatureStateStore"/>. Read-only: it never mutates state.
/// </summary>
public sealed class FeatureStateSettingsReader : IFeatureSettingsReader
{
    private readonly IFeatureStateStore _stateStore;

    /// <summary>Construct over the feature state store.</summary>
    public FeatureStateSettingsReader(IFeatureStateStore stateStore)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    /// <inheritdoc/>
    public async Task<string?> GetAsync(string featureId, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var state = await _stateStore.GetAsync(featureId, ct).ConfigureAwait(false);
        return state.Settings.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string?>> GetAllAsync(
        string featureId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);

        var state = await _stateStore.GetAsync(featureId, ct).ConfigureAwait(false);
        return state.Settings;
    }
}
