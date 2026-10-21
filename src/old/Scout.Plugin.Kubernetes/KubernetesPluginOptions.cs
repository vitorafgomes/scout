namespace Scout.Plugin.Kubernetes;

/// <summary>
/// Bound options for <see cref="KubernetesPlugin"/>. The two credential
/// keys mirror the catalog declared in
/// <c>Scout.Core.Features.FeatureCatalog</c>: a required kubeconfig
/// YAML blob and an optional context override.
/// </summary>
public sealed class KubernetesPluginOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Scout:Plugins:Kubernetes";

    /// <summary>Credential store key for the kubeconfig YAML body.</summary>
    public string KubeconfigCredentialKey { get; set; } = "scout/kubernetes/kubeconfig";

    /// <summary>Credential store key for the optional context override.</summary>
    public string ContextCredentialKey { get; set; } = "scout/kubernetes/context";

    /// <summary>Default tail length for pod log queries when not specified.</summary>
    public int DefaultLogTailLines { get; set; } = 200;

    /// <summary>Upper bound for pod log tail length; requests above this are clamped.</summary>
    public int MaxLogTailLines { get; set; } = 5000;

    /// <summary>Default HTTP timeout for the Kubernetes client.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Optional base URL for an external Kubernetes dashboard. When set, it
    /// is woven into citation URLs so chat consumers can deep-link.
    /// </summary>
    public string? DashboardBaseUrl { get; set; }
}
