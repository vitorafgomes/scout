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

/// <summary>What a feature does with one scope: read it, write it, or write it irreversibly.</summary>
public enum ScopeAccess
{
    /// <summary>Reads only.</summary>
    Read,

    /// <summary>A write that can be undone and runs under the feature's grant.</summary>
    Write,

    /// <summary>A write that cannot be undone; each action waits for the owner's approval.</summary>
    IrreversibleWrite,
}

/// <summary>
/// One row of a scope-to-actions matrix: <paramref name="Summary"/> is the one sentence a feature's details panel shows
/// for the row, and <paramref name="Actions"/> are every action of the row in full, which the write confirmation lists.
/// </summary>
public sealed record FeatureScopeRow(
    string Scope,
    string Summary,
    IReadOnlyList<string> Actions,
    ScopeAccess Access = ScopeAccess.Read);
