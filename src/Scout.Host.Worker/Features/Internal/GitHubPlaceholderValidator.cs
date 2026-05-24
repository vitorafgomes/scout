namespace Scout.Host.Worker.Features.Internal;

/// <summary>
/// Phase 1 placeholder. Real validator lands in Phase 2 together with
/// the GitHub plugin. Returns <see cref="FeatureCredentialValidationOutcome.Invalid"/>
/// for every input so the endpoint flow can be exercised end-to-end.
/// </summary>
internal sealed class GitHubPlaceholderValidator : IFeatureCredentialValidator
{
    /// <inheritdoc/>
    public string FeatureId => FeatureCatalog.GitHubId;

    /// <inheritdoc/>
    public Task<FeatureCredentialValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, string> credentialBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialBody);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new FeatureCredentialValidationResult(
            FeatureCredentialValidationOutcome.Invalid,
            "Validator lands in Phase 2."));
    }
}
