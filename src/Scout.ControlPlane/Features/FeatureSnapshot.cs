namespace Scout.ControlPlane.Features;

/// <summary>Resolved per-feature status reported to the UI.</summary>
public enum FeatureStatusKind
{
    Enabled,
    NeedsCredentials,
    Disabled,
    Error,
}

/// <summary>
/// Merged catalog + persisted state + credential-presence view of a feature.
/// The gRPC adapter maps this to the wire <c>Feature</c> message.
/// </summary>
public sealed record FeatureSnapshot(
    string Id,
    string DisplayName,
    string Description,
    bool RequiresCredentials,
    bool HasCredentials,
    bool Enabled,
    FeatureStatusKind Status,
    string? LastError,
    IReadOnlyList<FeatureCredentialField> CredentialFields,
    IReadOnlyList<FeatureSettingField> SettingsFields,
    IReadOnlyDictionary<string, string?> Settings,
    IReadOnlyList<FeatureScopeRow>? ScopeMatrix,
    IReadOnlyList<string>? SetupSteps);

/// <summary>Body of a setup request: credential upserts plus setting upserts/clears.</summary>
public sealed record FeatureSetupInput(
    IReadOnlyDictionary<string, string>? Credentials,
    IReadOnlyDictionary<string, string?>? Settings);
