namespace Scout.Host.Worker.Features;

/// <summary>
/// Catalog entry for one Scout feature. Hard-coded today; Phase 2/3 may
/// move this to plugin-declared metadata if the list grows beyond a few.
/// The <see cref="CredentialFields"/>, <see cref="SettingsFields"/> and
/// <see cref="ScopeMatrix"/> lists are the single source of truth: both
/// the worker's runtime logic and the UI-facing <see cref="FeatureDto"/>
/// render from the same shapes.
/// </summary>
public sealed record FeatureMetadata(
    string Id,
    string DisplayName,
    string Description,
    bool RequiresCredentials,
    IReadOnlyList<FeatureCredentialFieldDto> CredentialFields,
    IReadOnlyList<FeatureSettingFieldDto> SettingsFields,
    WriteClass WriteClass,
    IReadOnlyList<FeatureScopeRowDto>? ScopeMatrix = null,
    IReadOnlyList<string>? SetupSteps = null);
