using k8s.Models;

namespace Scout.Plugin.Kubernetes.Internal;

/// <summary>
/// Read-only wrapper over the KubernetesClient extension methods used by
/// <see cref="KubernetesFunctions"/>. NSubstitute cannot stub the K8s
/// extension methods directly, so the production implementation
/// delegates to them and tests substitute this interface instead.
/// </summary>
internal interface IKubernetesReadOps
{
    /// <summary>List all namespaces.</summary>
    Task<V1NamespaceList> ListNamespacesAsync(CancellationToken cancellationToken);

    /// <summary>List pods in a single namespace.</summary>
    Task<V1PodList> ListPodsAsync(string @namespace, CancellationToken cancellationToken);

    /// <summary>List pods across all namespaces.</summary>
    Task<V1PodList> ListPodsForAllNamespacesAsync(CancellationToken cancellationToken);

    /// <summary>Read a single pod by namespace + name.</summary>
    Task<V1Pod> ReadPodAsync(string name, string @namespace, CancellationToken cancellationToken);

    /// <summary>List events in a single namespace.</summary>
    Task<Corev1EventList> ListEventsAsync(string @namespace, CancellationToken cancellationToken);

    /// <summary>List events across all namespaces.</summary>
    Task<Corev1EventList> ListEventsForAllNamespacesAsync(CancellationToken cancellationToken);

    /// <summary>Read pod logs as a stream.</summary>
    Task<Stream> ReadPodLogsAsync(
        string name,
        string @namespace,
        string? container,
        int tailLines,
        int? sinceSeconds,
        CancellationToken cancellationToken);

    /// <summary>List deployments in a single namespace.</summary>
    Task<V1DeploymentList> ListDeploymentsAsync(string @namespace, CancellationToken cancellationToken);

    /// <summary>List deployments across all namespaces.</summary>
    Task<V1DeploymentList> ListDeploymentsForAllNamespacesAsync(CancellationToken cancellationToken);
}
