using k8s;
using k8s.Models;

namespace Scout.Plugin.Kubernetes.Internal;

/// <summary>
/// Default <see cref="IKubernetesReadOps"/>. Each method calls into the
/// generated KubernetesClient extension methods. No write verbs are
/// touched anywhere in this class; the type only exists to give Scout a
/// substitutable seam over the read-only surface.
/// </summary>
internal sealed class KubernetesReadOps : IKubernetesReadOps
{
    private readonly IKubernetesClientFactory _factory;

    public KubernetesReadOps(IKubernetesClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<V1NamespaceList> ListNamespacesAsync(CancellationToken cancellationToken)
    {
        var client = await _factory.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CoreV1.ListNamespaceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<V1PodList> ListPodsAsync(string @namespace, CancellationToken cancellationToken)
    {
        var client = await _factory.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: @namespace,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<V1PodList> ListPodsForAllNamespacesAsync(CancellationToken cancellationToken)
    {
        var client = await _factory.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<V1Pod> ReadPodAsync(string name, string @namespace, CancellationToken cancellationToken)
    {
        var client = await _factory.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CoreV1.ReadNamespacedPodAsync(
            name: name,
            namespaceParameter: @namespace,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<Corev1EventList> ListEventsAsync(string @namespace, CancellationToken cancellationToken)
    {
        var client = await _factory.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CoreV1.ListNamespacedEventAsync(
            namespaceParameter: @namespace,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<Corev1EventList> ListEventsForAllNamespacesAsync(CancellationToken cancellationToken)
    {
        var client = await _factory.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CoreV1.ListEventForAllNamespacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> ReadPodLogsAsync(
        string name,
        string @namespace,
        string? container,
        int tailLines,
        int? sinceSeconds,
        CancellationToken cancellationToken)
    {
        var client = await _factory.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CoreV1.ReadNamespacedPodLogAsync(
            name: name,
            namespaceParameter: @namespace,
            container: container,
            follow: false,
            tailLines: tailLines,
            sinceSeconds: sinceSeconds,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<V1DeploymentList> ListDeploymentsAsync(string @namespace, CancellationToken cancellationToken)
    {
        var client = await _factory.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.AppsV1.ListNamespacedDeploymentAsync(
            namespaceParameter: @namespace,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<V1DeploymentList> ListDeploymentsForAllNamespacesAsync(CancellationToken cancellationToken)
    {
        var client = await _factory.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.AppsV1.ListDeploymentForAllNamespacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
