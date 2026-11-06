namespace Scout.ControlPlane.Features;

/// <summary>Thrown when a feature id is not in the catalog.</summary>
public sealed class FeatureUnknownException(string id) : Exception($"Unknown feature '{id}'.")
{
    public string FeatureId { get; } = id;
}

/// <summary>
/// Thrown when a setup/credentials request fails validation. Each entry in
/// <see cref="Errors"/> uses a dotted-path key (<c>credentials.&lt;name&gt;</c>
/// or <c>settings.&lt;name&gt;</c>) so the UI can highlight the field.
/// </summary>
public sealed class FeatureValidationFailedException(IReadOnlyDictionary<string, string> errors) : Exception("Feature request failed validation.")
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>Thrown when persisting credentials or settings fails.</summary>
public sealed class FeaturePersistFailedException(string message) : Exception(message)
{
}

/// <summary>Thrown when enable is called without the required credentials.</summary>
public sealed class FeatureCredentialsMissingException(string id) : Exception("Add credentials before enabling this feature.")
{
    public string FeatureId { get; } = id;
}

/// <summary>
/// Thrown when enabling or granting a feature with write rows comes without the
/// owner's confirmation of those writes.
/// </summary>
/// <param name="id">The feature id.</param>
/// <param name="writeRows">The rows the owner must confirm.</param>
/// <param name="before">What the confirmation precedes, such as "enabling it" or "granting them".</param>
public sealed class WriteConfirmationRequiredException(string id, IReadOnlyList<FeatureScopeRow> writeRows, string before) : Exception($"Confirm the writes of {id} before {before}: {string.Join(", ", writeRows.Select(Describe))}.")
{
    public string FeatureId { get; } = id;

    public IReadOnlyList<FeatureScopeRow> WriteRows { get; } = writeRows;

    private static string Describe(FeatureScopeRow row) =>
        row.Access == ScopeAccess.IrreversibleWrite
            ? $"{row.Scope} (irreversible write)"
            : $"{row.Scope} (write)";
}

/// <summary>Thrown when enable targets a feature whose plugin is not wired yet.</summary>
public sealed class FeatureNotImplementedException(string id) : Exception("This feature's plugin has not been wired yet.")
{
    public string FeatureId { get; } = id;
}
