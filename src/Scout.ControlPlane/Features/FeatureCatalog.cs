namespace Scout.ControlPlane.Features;

/// <summary>Hard-coded catalog of Scout features.</summary>
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
                Key: "pat",
                Kind: "password",
                Label: "Personal Access Token",
                HelperText: "Required scopes: repo, read:org. Token is stored only on this machine (OS keyring) and never sent to Scout's developers.",
                CreateLinkUrl: "https://github.com/settings/tokens/new?scopes=repo,read:org&description=Scout",
                CreateLinkLabel: "Create token on GitHub"),
        },
        SettingsFields: new[]
        {
            new FeatureSettingField(
                Key: "default_repo_url",
                Kind: "text",
                Label: "Default repository URL",
                HelperText: "Used when a question doesn't name a repo (e.g. \"list open PRs\"). Paste the full URL - for example https://github.com/octocat/Hello-World.",
                CreateLinkUrl: null,
                CreateLinkLabel: null,
                Required: true),
        },
        WriteClass: WriteClass.None,
        ScopeMatrix: new[]
        {
            new FeatureScopeRow(
                Scope: "repo",
                Actions: new[]
                {
                    "List private and public repositories",
                    "Read pull requests with diff and file list",
                    "Read issues with comments",
                    "Create issues (descriptive write per ADR)",
                }),
            new FeatureScopeRow(
                Scope: "public_repo",
                Actions: new[] { "Subset of repo limited to public repositories only" }),
            new FeatureScopeRow(
                Scope: "read:org",
                Actions: new[] { "List repositories from organizations you belong to" }),
            new FeatureScopeRow(
                Scope: "read:user",
                Actions: new[] { "Identify the token owner (validation only)" }),
        },
        SetupSteps: new[]
        {
            "Click 'Create token on GitHub' below. GitHub opens a token-creation page with the scopes 'repo' and 'read:org' already selected.",
            "Scroll to the bottom and click 'Generate token'. Copy the generated value (it is shown only once).",
            "Paste the token into the 'Personal Access Token' field above.",
            "Click Save. Scout validates the token against GitHub before storing it on this machine's keyring.",
        });

    private static readonly FeatureMetadata Kubernetes = new(
        Id: KubernetesId,
        DisplayName: "Kubernetes",
        Description: "Describe pods, nodes and workloads across configured clusters.",
        RequiresCredentials: true,
        CredentialFields: new[]
        {
            new FeatureCredentialField(
                Key: "kubeconfig",
                Kind: "textarea",
                Label: "Kubeconfig YAML",
                HelperText: "Paste the contents of your kubeconfig file. Embed credentials inline (certificate-authority-data, client-certificate-data, token) - external file references and exec-plugin auth are not supported.",
                CreateLinkUrl: null,
                CreateLinkLabel: null),
            new FeatureCredentialField(
                Key: "context",
                Kind: "text",
                Label: "Active context (optional)",
                HelperText: "Leave blank to use the default context from the kubeconfig. Filled in automatically when the kubeconfig has multiple contexts.",
                CreateLinkUrl: null,
                CreateLinkLabel: null),
        },
        SettingsFields: Array.Empty<FeatureSettingField>(),
        WriteClass: WriteClass.None,
        ScopeMatrix: null,
        SetupSteps: new[]
        {
            "Run 'kubectl config view --raw --flatten' on a terminal that already has working cluster access. The '--flatten' flag inlines certificates and tokens so the kubeconfig is self-contained.",
            "Copy the full YAML output to the clipboard.",
            "Paste it into the 'Kubeconfig YAML' field above.",
            "If your kubeconfig has more than one context, type the context name in the 'Active context (optional)' field. Leave blank to use the default.",
            "Click Save. Scout calls the cluster's /version endpoint to confirm reachability and credential validity before storing on this machine's keyring.",
        });

    private static readonly IReadOnlyList<FeatureMetadata> AllFeatures = new[] { GitHub, Kubernetes };

    private readonly Dictionary<string, FeatureMetadata> _byId = AllFeatures.ToDictionary(
        f => f.Id,
        StringComparer.Ordinal);

    /// <inheritdoc/>
    public IReadOnlyList<FeatureMetadata> All => AllFeatures;

    /// <inheritdoc/>
    public FeatureMetadata? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _byId.GetValueOrDefault(id);
    }
}
