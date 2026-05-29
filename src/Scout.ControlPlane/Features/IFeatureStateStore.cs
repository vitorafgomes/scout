namespace Scout.ControlPlane.Features;

/// <summary>
/// Persistent state for a single feature. <c>LastError</c> is null when the
/// feature is healthy. <c>Settings</c> carries the non-secret configuration
/// values the user typed into the setup dialog; empty when the feature has no
/// settings or has never been configured.
/// </summary>
public sealed record FeatureState(
    bool Enabled,
    string? LastError,
    IReadOnlyDictionary<string, string?> Settings);

/// <summary>
/// Persists per-feature toggle and last-error state across restarts.
/// Missing entries default to <c>Enabled = false, LastError = null</c>.
/// </summary>
public interface IFeatureStateStore
{
    /// <summary>Read state for <paramref name="featureId"/>.</summary>
    Task<FeatureState> GetAsync(string featureId, CancellationToken ct);

    /// <summary>Persist state for <paramref name="featureId"/>.</summary>
    Task SetAsync(string featureId, FeatureState state, CancellationToken ct);

    /// <summary>Snapshot of every persisted entry.</summary>
    Task<IReadOnlyDictionary<string, FeatureState>> GetAllAsync(CancellationToken ct);
}

/// <summary>
/// Helpers that build <see cref="FeatureState"/> instances with a consistent
/// empty <see cref="FeatureState.Settings"/> shape.
/// </summary>
public static class FeatureStates
{
    /// <summary>Empty, ordinal-keyed settings dictionary.</summary>
    public static Dictionary<string, string?> EmptySettings() =>
        new(StringComparer.Ordinal);

    /// <summary>Default state for a feature that has never been touched.</summary>
    public static FeatureState EmptyDisabled() =>
        new(false, null, EmptySettings());
}
