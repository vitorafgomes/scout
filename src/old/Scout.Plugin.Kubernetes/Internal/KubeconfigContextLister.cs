using System.Text;
using k8s;

namespace Scout.Plugin.Kubernetes.Internal;

/// <summary>
/// Helper that lists the context names declared in a kubeconfig YAML
/// blob. Used by the validator to surface the available contexts back
/// to the UI and by future credential-picker flows.
/// </summary>
public static class KubeconfigContextLister
{
    /// <summary>
    /// Parses <paramref name="yaml"/> and returns the declared context
    /// names in declaration order.
    /// </summary>
    /// <exception cref="ArgumentException">When <paramref name="yaml"/> is empty.</exception>
    public static IReadOnlyList<string> ListContexts(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(yaml));
        var cfg = KubernetesClientConfiguration.LoadKubeConfig(stream);

        if (cfg?.Contexts is null)
        {
            return Array.Empty<string>();
        }

        return cfg.Contexts
            .Where(c => !string.IsNullOrWhiteSpace(c?.Name))
            .Select(c => c.Name)
            .ToArray();
    }
}
