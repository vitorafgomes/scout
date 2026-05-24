namespace Scout.Host.Worker.Features;

/// <summary>
/// Declarative description of a feature credential input. The renderer
/// builds its form purely from this metadata.
/// </summary>
public sealed record FeatureCredentialField(
    string Name,
    string DisplayName,
    FeatureFieldKind Kind,
    bool Required,
    string? HelpText = null);

/// <summary>
/// Catalog entry for one Scout feature. Hard-coded today; Phase 2/3 may
/// move this to plugin-declared metadata if the list grows beyond a few.
/// </summary>
public sealed record FeatureMetadata(
    string Id,
    string DisplayName,
    string Description,
    bool RequiresCredentials,
    IReadOnlyList<FeatureCredentialField> CredentialFields,
    WriteClass WriteClass);
