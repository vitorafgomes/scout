namespace Scout.ControlPlane.Features;

/// <summary>Thrown when a feature id is not in the catalog.</summary>
public sealed class FeatureUnknownException : Exception
{
    public string FeatureId { get; }

    public FeatureUnknownException(string id)
        : base($"Unknown feature '{id}'.")
    {
        FeatureId = id;
    }
}

/// <summary>
/// Thrown when a setup/credentials request fails validation. Each entry in
/// <see cref="Errors"/> uses a dotted-path key (<c>credentials.&lt;name&gt;</c>
/// or <c>settings.&lt;name&gt;</c>) so the UI can highlight the field.
/// </summary>
public sealed class FeatureValidationFailedException : Exception
{
    public IReadOnlyDictionary<string, string> Errors { get; }

    public FeatureValidationFailedException(IReadOnlyDictionary<string, string> errors)
        : base("Feature request failed validation.")
    {
        Errors = errors;
    }
}

/// <summary>Thrown when persisting credentials or settings fails.</summary>
public sealed class FeaturePersistFailedException : Exception
{
    public FeaturePersistFailedException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when enable is called without the required credentials.</summary>
public sealed class FeatureCredentialsMissingException : Exception
{
    public string FeatureId { get; }

    public FeatureCredentialsMissingException(string id)
        : base("Add credentials before enabling this feature.")
    {
        FeatureId = id;
    }
}

/// <summary>
/// Thrown when enabling or granting a feature with write rows comes without the
/// owner's confirmation of those writes.
/// </summary>
public sealed class WriteConfirmationRequiredException : Exception
{
    public string FeatureId { get; }

    public IReadOnlyList<FeatureScopeRow> WriteRows { get; }

    /// <param name="id">The feature id.</param>
    /// <param name="writeRows">The rows the owner must confirm.</param>
    /// <param name="before">What the confirmation precedes, such as "enabling it" or "granting them".</param>
    public WriteConfirmationRequiredException(string id, IReadOnlyList<FeatureScopeRow> writeRows, string before)
        : base($"Confirm the writes of {id} before {before}: {string.Join(", ", writeRows.Select(Describe))}.")
    {
        FeatureId = id;
        WriteRows = writeRows;
    }

    private static string Describe(FeatureScopeRow row) =>
        row.Access == ScopeAccess.IrreversibleWrite
            ? $"{row.Scope} (irreversible write)"
            : $"{row.Scope} (write)";
}

/// <summary>Thrown when enable targets a feature whose plugin is not wired yet.</summary>
public sealed class FeatureNotImplementedException : Exception
{
    public string FeatureId { get; }

    public FeatureNotImplementedException(string id)
        : base("This feature's plugin has not been wired yet.")
    {
        FeatureId = id;
    }
}
