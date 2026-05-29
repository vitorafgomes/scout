namespace Scout.ControlPlane.Features;

/// <summary>
/// Options for <see cref="JsonFileFeatureStateStore"/>. Bound from
/// <c>Scout:Features:StateStore</c>. Tests set <see cref="FilePath"/> to a
/// disposable temp file.
/// </summary>
public sealed class FeatureStateStoreOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Scout:Features:StateStore";

    /// <summary>
    /// Absolute path of the JSON file. When null the store falls back to
    /// <c>%LocalAppData%/Scout/features.json</c>.
    /// </summary>
    public string? FilePath { get; set; }
}
