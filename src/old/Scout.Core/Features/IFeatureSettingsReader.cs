namespace Scout.Core.Features;

/// <summary>
/// Read-only view over a feature's persisted non-secret settings. Plugins
/// reference <c>Scout.Core</c> only, so this seam lets them resolve values
/// (such as a default repository) without seeing the worker's
/// <c>IFeatureStateStore</c>.
/// </summary>
public interface IFeatureSettingsReader
{
    /// <summary>
    /// Reads a single setting value for <paramref name="featureId"/>, or
    /// null when the feature or the key is absent (or explicitly cleared).
    /// </summary>
    Task<string?> GetAsync(string featureId, string key, CancellationToken ct);

    /// <summary>
    /// Reads every persisted setting for <paramref name="featureId"/>.
    /// Returns an empty map when the feature has no settings.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> GetAllAsync(string featureId, CancellationToken ct);
}
