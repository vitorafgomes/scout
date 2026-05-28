namespace Scout.Host.Worker.Features;

/// <summary>
/// Helpers that build <see cref="FeatureState"/> instances with a
/// consistent empty <see cref="FeatureState.Settings"/> shape. Keeping
/// the construction in one place avoids accidental drift between
/// callers that would otherwise hand-roll dictionaries with different
/// string comparers.
/// </summary>
internal static class FeatureStates
{
    /// <summary>Empty, ordinal-keyed settings dictionary.</summary>
    public static Dictionary<string, string?> EmptySettings() =>
        new(StringComparer.Ordinal);

    /// <summary>Default state for a feature that has never been touched.</summary>
    public static FeatureState EmptyDisabled() =>
        new(false, null, EmptySettings());

    /// <summary>Enabled state with no error and no settings.</summary>
    public static FeatureState EnabledWithoutSettings() =>
        new(true, null, EmptySettings());

    /// <summary>Disabled state carrying an error message.</summary>
    public static FeatureState DisabledWithError(string error) =>
        new(false, error, EmptySettings());

    /// <summary>Enabled state carrying an error message.</summary>
    public static FeatureState EnabledWithError(string error) =>
        new(true, error, EmptySettings());
}
