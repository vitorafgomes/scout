namespace Scout.Host.Worker.Features;

/// <summary>
/// Hard-coded catalog. Phase 2 promotes <c>github</c>'s WriteClass to
/// <see cref="WriteClass.Descriptive"/> when CreateIssue lands.
/// </summary>
public sealed class FeatureCatalog : IFeatureCatalog
{
    /// <summary>Canonical id for the GitHub feature.</summary>
    public const string GitHubId = "github";

    /// <summary>Canonical id for the Kubernetes feature.</summary>
    public const string KubernetesId = "kubernetes";

    private static readonly FeatureMetadata GitHub = new(
        Id: GitHubId,
        DisplayName: "GitHub",
        Description: "Query repositories, issues and pull requests from chat.",
        RequiresCredentials: true,
        CredentialFields: new[]
        {
            new FeatureCredentialField(
                Name: "pat",
                DisplayName: "Personal Access Token",
                Kind: FeatureFieldKind.SingleLineSecret,
                Required: true,
                HelpText: "Classic PAT or fine-grained token with read scopes."),
        },
        WriteClass: WriteClass.None);

    private static readonly FeatureMetadata Kubernetes = new(
        Id: KubernetesId,
        DisplayName: "Kubernetes",
        Description: "Describe pods, nodes and workloads across configured clusters.",
        RequiresCredentials: true,
        CredentialFields: new[]
        {
            new FeatureCredentialField(
                Name: "kubeconfig",
                DisplayName: "Kubeconfig",
                Kind: FeatureFieldKind.MultiLineSecret,
                Required: true,
                HelpText: "Full YAML kubeconfig. Exec-plugin auth is rejected."),
            new FeatureCredentialField(
                Name: "context",
                DisplayName: "Context",
                Kind: FeatureFieldKind.SingleLineSecret,
                Required: false,
                HelpText: "Optional kubeconfig context. Defaults to current-context."),
        },
        WriteClass: WriteClass.None);

    private static readonly IReadOnlyList<FeatureMetadata> _all = new[] { GitHub, Kubernetes };

    private readonly Dictionary<string, FeatureMetadata> _byId = _all.ToDictionary(
        f => f.Id,
        StringComparer.Ordinal);

    /// <inheritdoc/>
    public IReadOnlyList<FeatureMetadata> All => _all;

    /// <inheritdoc/>
    public FeatureMetadata? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _byId.GetValueOrDefault(id);
    }
}
