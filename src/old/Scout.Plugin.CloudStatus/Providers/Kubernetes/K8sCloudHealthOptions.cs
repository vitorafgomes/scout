namespace Scout.Plugin.CloudStatus.Providers.Kubernetes;

/// <summary>
/// Settings for <see cref="K8sCloudHealthProvider"/>. Bound from
/// "Scout:Plugins:CloudStatus:Kubernetes". Configuration is read-only by
/// design — the provider only calls List / Get endpoints.
/// </summary>
public sealed class K8sCloudHealthOptions
{
    /// <summary>
    /// Logical name of the cluster — used as <see cref="Domain.StatusReport.Scope"/>.
    /// Defaults to <c>"default"</c>; override per environment (e.g. "prod-eks").
    /// </summary>
    public string ClusterName { get; set; } = "default";

    /// <summary>
    /// When <c>true</c>, load configuration from the pod's service account
    /// (in-cluster). Mutually exclusive with <see cref="KubeconfigPath"/>.
    /// </summary>
    public bool UseInClusterConfig { get; set; }

    /// <summary>
    /// Explicit kubeconfig file path. When null or empty, and
    /// <see cref="UseInClusterConfig"/> is false, the default KUBECONFIG
    /// discovery chain is used (<c>~/.kube/config</c>, etc.).
    /// </summary>
    public string? KubeconfigPath { get; set; }

    /// <summary>
    /// Kubeconfig context name to activate. <c>null</c> = use the current
    /// context.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// Namespaces to inspect for pod health. Empty means all namespaces.
    /// Matching is case-sensitive (Kubernetes names are).
    /// </summary>
    public IList<string> Namespaces { get; set; } = new List<string>();

    /// <summary>Include node readiness checks in the report.</summary>
    public bool CheckNodes { get; set; } = true;

    /// <summary>
    /// HTTP timeout per API call. Keep low — the bot should not block
    /// a chat reply for long if the cluster is unreachable.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}
