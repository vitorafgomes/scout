namespace Scout.Host.Worker.Features;

/// <summary>
/// Body of <c>POST /features/{id}/setup</c>. Carries both credential
/// values (secrets, written to the OS keyring) and setting values
/// (non-secret, persisted in clear in the feature state store). Either
/// section may be <see langword="null"/> when the feature has no
/// matching catalog entries.
/// </summary>
/// <param name="Credentials">Map of credential key to value. The keys
/// must match <see cref="FeatureMetadata.CredentialFields"/> entries.</param>
/// <param name="Settings">Map of setting key to value. The keys must
/// match <see cref="FeatureMetadata.SettingsFields"/> entries. A value
/// of <see langword="null"/> means "clear this setting".</param>
public sealed record FeatureSetupRequest(
    IReadOnlyDictionary<string, string>? Credentials,
    IReadOnlyDictionary<string, string?>? Settings);

/// <summary>
/// Error payload returned by <c>POST /features/{id}/setup</c> when the
/// request body fails validation. Each entry in <see cref="Errors"/>
/// uses a dotted-path key (<c>credentials.&lt;name&gt;</c> or
/// <c>settings.&lt;name&gt;</c>) so the UI can highlight the offending
/// field without re-deriving the section.
/// </summary>
/// <param name="Code">Machine-readable error code
/// (e.g. <c>features/validation-failed</c>).</param>
/// <param name="Errors">Per-field error messages keyed by dotted path.</param>
public sealed record FeatureSetupErrorResponse(
    string Code,
    IReadOnlyDictionary<string, string> Errors);
