namespace Scout.Host.Worker.Features;

/// <summary>
/// One credential input the UI must render in the dialog. Drives a
/// fully schema-driven form: the renderer never hard-codes field names.
/// </summary>
/// <param name="Key">Stable identifier used as the credential key
/// (e.g. <c>pat</c>, <c>kubeconfig</c>). Also the JSON property name in
/// the <c>POST /features/{id}/credentials</c> body.</param>
/// <param name="Kind">Widget hint: <c>text</c>, <c>password</c> or
/// <c>textarea</c>. Anything other than <c>text</c> is treated as a
/// required, sensitive field by the worker.</param>
/// <param name="Label">Human-readable label shown above the input.</param>
/// <param name="HelperText">Optional secondary text rendered beneath
/// the input.</param>
/// <param name="CreateLinkUrl">Optional deep link that takes the user
/// to the provider page where the credential can be minted.</param>
/// <param name="CreateLinkLabel">Display text for
/// <paramref name="CreateLinkUrl"/>.</param>
public sealed record FeatureCredentialFieldDto(
    string Key,
    string Kind,
    string Label,
    string? HelperText,
    string? CreateLinkUrl,
    string? CreateLinkLabel);

/// <summary>
/// One row of a scope-to-actions matrix the UI renders as an
/// informational table inside the credential dialog. Optional per
/// feature; <see langword="null"/> on <see cref="FeatureDto.ScopeMatrix"/>
/// means the feature has no scope vocabulary worth showing.
/// </summary>
/// <param name="Scope">Provider-specific scope name (e.g. GitHub
/// <c>repo</c>).</param>
/// <param name="Actions">Human-readable actions enabled by the
/// scope.</param>
public sealed record FeatureScopeRowDto(
    string Scope,
    IReadOnlyList<string> Actions);

/// <summary>
/// Wire shape returned by <c>/features</c>. Mirrors the Angular
/// <c>FeatureDto</c> contract in <c>ui/packages/shared</c>.
/// </summary>
public sealed record FeatureDto(
    string Id,
    string DisplayName,
    string Description,
    bool RequiresCredentials,
    bool HasCredentials,
    bool Enabled,
    string Status,
    string? LastError,
    IReadOnlyList<FeatureCredentialFieldDto> CredentialFields,
    IReadOnlyList<FeatureScopeRowDto>? ScopeMatrix,
    IReadOnlyList<string>? SetupSteps);
