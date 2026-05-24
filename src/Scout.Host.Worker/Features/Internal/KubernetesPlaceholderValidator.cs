namespace Scout.Host.Worker.Features.Internal;

/// <summary>
/// Phase 1 placeholder. Real validator lands in Phase 3 together with
/// the Kubernetes plugin. Returns <see cref="FeatureCredentialValidationOutcome.Invalid"/>
/// for every input so the endpoint flow can be exercised end-to-end.
/// </summary>
internal sealed class KubernetesPlaceholderValidator : IFeatureCredentialValidator
{
    /// <inheritdoc/>
    public string FeatureId => FeatureCatalog.KubernetesId;

    /// <inheritdoc/>
    public Task<FeatureCredentialValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, string> credentialBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialBody);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new FeatureCredentialValidationResult(
            FeatureCredentialValidationOutcome.Invalid,
            "Validator lands in Phase 3."));
    }
}
