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
                HelperText: "Only for a Scout host without the desktop sign-in. Use a fine-grained token with read-only access to Metadata, Contents, Pull requests and Issues.",
                CreateLinkUrl: null,
                CreateLinkLabel: null),
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
                Scope: "Metadata: read",
                Actions: new[] { "List the repositories the Scout GitHub App is installed on" }),
            new FeatureScopeRow(
                Scope: "Contents: read",
                Actions: new[] { "Read the README, languages and top-level files of a repository" }),
            new FeatureScopeRow(
                Scope: "Pull requests: read",
                Actions: new[] { "List open pull requests" }),
            new FeatureScopeRow(
                Scope: "Issues: read",
                Actions: new[] { "List open issues" }),
        },
        SetupSteps: new[]
        {
            "Click 'Connect with GitHub'. Scout shows a one-time code.",
            "Click 'Open GitHub', sign in with your GitHub account and enter the code.",
            "Approve the Scout GitHub App. It asks for read access only; Scout never writes to GitHub.",
            "If the card says so, click 'Install Scout on your GitHub account' and pick the repositories Scout may read.",
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
