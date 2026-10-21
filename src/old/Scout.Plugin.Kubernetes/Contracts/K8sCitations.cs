using Scout.Core.Chat;

namespace Scout.Plugin.Kubernetes.Contracts;

/// <summary>
/// Citation builders for Kubernetes function results. Citations describe
/// the operator-facing equivalent (a kubectl command) so the chat reply
/// can be reproduced manually, plus an optional dashboard deep-link.
/// </summary>
public static class K8sCitations
{
    /// <summary>Citation for a namespace.</summary>
    public static Citation ForNamespace(string name, string? dashboardBaseUrl = null)
        => new($"kubectl get ns {name}")
        {
            Path = $"namespaces/{name}",
            Url = Combine(dashboardBaseUrl, $"/namespaces/{name}"),
        };

    /// <summary>Citation for a pod.</summary>
    public static Citation ForPod(string ns, string name, string? dashboardBaseUrl = null)
        => new($"kubectl describe pod -n {ns} {name}")
        {
            Path = $"pods/{ns}/{name}",
            Url = Combine(dashboardBaseUrl, $"/pods/{ns}/{name}"),
        };

    /// <summary>Citation for a deployment.</summary>
    public static Citation ForDeployment(string ns, string name, string? dashboardBaseUrl = null)
        => new($"kubectl describe deployment -n {ns} {name}")
        {
            Path = $"deployments/{ns}/{name}",
            Url = Combine(dashboardBaseUrl, $"/deployments/{ns}/{name}"),
        };

    /// <summary>Citation for an event listing.</summary>
    public static Citation ForEvents(string? ns, int? sinceSeconds, string? dashboardBaseUrl = null)
    {
        var scope = string.IsNullOrWhiteSpace(ns) ? "-A" : $"-n {ns}";
        var sinceFragment = sinceSeconds is { } s ? $" --since={s}s" : string.Empty;
        return new($"kubectl get events {scope}{sinceFragment}")
        {
            Path = ns is null ? "events" : $"events/{ns}",
            Url = Combine(dashboardBaseUrl, ns is null ? "/events" : $"/events/{ns}"),
        };
    }

    /// <summary>Citation for a pod log query.</summary>
    public static Citation ForLogs(string ns, string pod, string? container, int tailLines, string? dashboardBaseUrl = null)
    {
        var containerFragment = string.IsNullOrWhiteSpace(container) ? string.Empty : $" -c {container}";
        return new($"kubectl logs -n {ns} {pod}{containerFragment} --tail={tailLines}")
        {
            Path = $"pods/{ns}/{pod}/logs",
            Url = Combine(dashboardBaseUrl, $"/pods/{ns}/{pod}/logs"),
        };
    }

    private static string? Combine(string? baseUrl, string suffix)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return baseUrl.TrimEnd('/') + suffix;
    }
}
