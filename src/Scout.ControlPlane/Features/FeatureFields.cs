namespace Scout.ControlPlane.Features;

/// <summary>
/// One credential input the UI renders in the setup dialog. Drives a
/// fully schema-driven form: the renderer never hard-codes field names.
/// </summary>
public sealed record FeatureCredentialField(
    string Key,
    string Kind,
    string Label,
    string? HelperText,
    string? CreateLinkUrl,
    string? CreateLinkLabel);

/// <summary>
/// One non-secret configuration input the UI renders in the setup dialog.
/// Values persist in clear in the feature state store.
/// </summary>
public sealed record FeatureSettingField(
    string Key,
    string Kind,
    string Label,
    string? HelperText,
    string? CreateLinkUrl,
    string? CreateLinkLabel,
    bool Required);

/// <summary>One row of a scope-to-actions matrix rendered as a table.</summary>
public sealed record FeatureScopeRow(
    string Scope,
    IReadOnlyList<string> Actions);
