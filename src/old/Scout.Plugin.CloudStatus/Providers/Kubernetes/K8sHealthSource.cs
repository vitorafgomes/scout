using k8s;
using Microsoft.Extensions.Options;

namespace Scout.Plugin.CloudStatus.Providers.Kubernetes;

/// <summary>
/// Default <see cref="IK8sHealthSource"/>. Loads Kubernetes configuration
/// from the configured source (in-cluster service account, explicit
/// kubeconfig path, or default discovery) and builds an <see cref="IKubernetes"/>
/// client so authentication and endpoint resolution are in place for the
/// first real query.
///
/// <para>
/// The live node / pod listing call is tracked as a follow-up issue in the
/// source repository. Today <see cref="GetActiveIssuesAsync"/> returns an
/// empty list, which <see cref="K8sCloudHealthProvider"/> maps to
/// <see cref="Domain.HealthLevel.Healthy"/>. The seam keeps the swap
/// isolated — the live SDK calls can land without touching
/// <see cref="K8sCloudHealthProvider"/> or its tests.
/// </para>
/// </summary>
public sealed class K8sHealthSource : IK8sHealthSource, IDisposable
{
    private readonly IKubernetes _client;

    public K8sHealthSource(IOptions<K8sCloudHealthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);

        var config = BuildConfig(options.Value);
        config.HttpClientTimeout = options.Value.Timeout;
        _client = new k8s.Kubernetes(config);
    }

    public Task<IReadOnlyList<K8sHealthIssue>> GetActiveIssuesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _client;
        return Task.FromResult<IReadOnlyList<K8sHealthIssue>>(Array.Empty<K8sHealthIssue>());
    }

    public void Dispose() => _client.Dispose();

    private static KubernetesClientConfiguration BuildConfig(K8sCloudHealthOptions options)
    {
        if (options.UseInClusterConfig)
        {
            return KubernetesClientConfiguration.InClusterConfig();
        }

        if (!string.IsNullOrWhiteSpace(options.KubeconfigPath))
        {
            return KubernetesClientConfiguration.BuildConfigFromConfigFile(
                kubeconfigPath: options.KubeconfigPath,
                currentContext: options.Context);
        }

        return string.IsNullOrWhiteSpace(options.Context)
            ? KubernetesClientConfiguration.BuildDefaultConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile(
                kubeconfigPath: null,
                currentContext: options.Context);
    }
}
