using System.ComponentModel;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Plugin.Kubernetes.Contracts;
using Scout.Plugin.Kubernetes.Internal;

namespace Scout.Plugin.Kubernetes;

/// <summary>
/// Read-only Semantic Kernel functions exposed by <see cref="KubernetesPlugin"/>.
/// Every function maps a <c>kubectl</c>-style query to a slim DTO; no
/// mutating verb is invoked anywhere in this class.
/// </summary>
public sealed class KubernetesFunctions
{
    private const int MaxRecentEvents = 20;

    private readonly IKubernetesReadOps _readOps;
    private readonly ILogger<KubernetesFunctions> _logger;
    private readonly IOptions<KubernetesPluginOptions> _options;

    internal KubernetesFunctions(
        IKubernetesReadOps readOps,
        ILogger<KubernetesFunctions> logger,
        IOptions<KubernetesPluginOptions> options)
    {
        ArgumentNullException.ThrowIfNull(readOps);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _readOps = readOps;
        _logger = logger;
        _options = options;
    }

    /// <summary>List namespaces visible to the configured kubeconfig.</summary>
    [KernelFunction("list_namespaces")]
    [Description("Read-only: list all namespaces visible to the configured kubeconfig.")]
    public async Task<IReadOnlyList<K8sNamespaceSummary>> ListNamespacesAsync(
        CancellationToken cancellationToken)
    {
        var dashboard = _options.Value.DashboardBaseUrl;
        var list = await _readOps.ListNamespacesAsync(cancellationToken).ConfigureAwait(false);
        if (list?.Items is null)
        {
            return Array.Empty<K8sNamespaceSummary>();
        }

        return list.Items
            .Select(n => new K8sNamespaceSummary(
                Name: n.Metadata?.Name ?? string.Empty,
                Status: n.Status?.Phase ?? "Unknown",
                CreatedAt: n.Metadata?.CreationTimestamp ?? DateTime.MinValue,
                Citation: K8sCitations.ForNamespace(n.Metadata?.Name ?? string.Empty, dashboard)))
            .ToArray();
    }

    /// <summary>List pods, optionally scoped to a namespace.</summary>
    [KernelFunction("list_pods")]
    [Description("Read-only: list pods in a namespace, or across all namespaces when 'namespace' is omitted.")]
    public async Task<IReadOnlyList<K8sPodSummary>> ListPodsAsync(
        [Description("Optional namespace. Leave blank to list across all namespaces.")] string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var dashboard = _options.Value.DashboardBaseUrl;
        var list = string.IsNullOrWhiteSpace(@namespace)
            ? await _readOps.ListPodsForAllNamespacesAsync(cancellationToken).ConfigureAwait(false)
            : await _readOps.ListPodsAsync(@namespace, cancellationToken).ConfigureAwait(false);

        if (list?.Items is null)
        {
            return Array.Empty<K8sPodSummary>();
        }

        return list.Items.Select(p => MapPodSummary(p, dashboard)).ToArray();
    }

    /// <summary>Describe a single pod with its recent events.</summary>
    [KernelFunction("describe_pod")]
    [Description("Read-only: describe a single pod with container status and the last 20 events.")]
    public async Task<K8sPodDescription> DescribePodAsync(
        [Description("Pod namespace.")] string @namespace,
        [Description("Pod name.")] string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var dashboard = _options.Value.DashboardBaseUrl;

        var pod = await _readOps.ReadPodAsync(name, @namespace, cancellationToken).ConfigureAwait(false);
        var events = await _readOps.ListEventsAsync(@namespace, cancellationToken).ConfigureAwait(false);

        var summary = MapPodSummary(pod, dashboard);
        var containers = MapContainerStatuses(pod);

        var recent = (events?.Items ?? Array.Empty<Corev1Event>())
            .Where(e => string.Equals(e.InvolvedObject?.Kind, "Pod", StringComparison.Ordinal)
                && string.Equals(e.InvolvedObject?.Name, name, StringComparison.Ordinal))
            .OrderByDescending(e => e.LastTimestamp ?? e.EventTime ?? DateTime.MinValue)
            .Take(MaxRecentEvents)
            .Select(MapEvent)
            .ToArray();

        var labels = pod.Metadata?.Labels is { } l
            ? new Dictionary<string, string>(l, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var annotations = pod.Metadata?.Annotations is { } a
            ? new Dictionary<string, string>(a, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new K8sPodDescription(
            Summary: summary,
            Containers: containers,
            RecentEvents: recent,
            Labels: labels,
            Annotations: annotations,
            Citation: K8sCitations.ForPod(@namespace, name, dashboard));
    }

    /// <summary>Read pod logs with tail clamping.</summary>
    [KernelFunction("get_pod_logs")]
    [Description("Read-only: tail logs from a pod container; tailLines is clamped to a safe upper bound.")]
    public async Task<K8sLogResult> GetPodLogsAsync(
        [Description("Pod namespace.")] string @namespace,
        [Description("Pod name.")] string pod,
        [Description("Container name. When omitted, defaults to the only container in the pod.")] string? container = null,
        [Description("Number of log lines to tail.")] int tailLines = 200,
        [Description("Optional seconds-since-now lower bound.")] int? sinceSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(pod);

        var opts = _options.Value;
        var dashboard = opts.DashboardBaseUrl;

        var requested = tailLines <= 0 ? opts.DefaultLogTailLines : tailLines;
        var clamped = Math.Min(Math.Max(requested, 1), opts.MaxLogTailLines);
        var truncated = clamped != requested;

        await using var stream = await _readOps.ReadPodLogsAsync(
            name: pod,
            @namespace: @namespace,
            container: container,
            tailLines: clamped,
            sinceSeconds: sinceSeconds,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(stream);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (truncated)
        {
            body = $"[scout: tailLines clamped from {requested} to {clamped}]\n" + body;
        }

        var lineCount = string.IsNullOrEmpty(body) ? 0 : body.Count(c => c == '\n') + 1;

        return new K8sLogResult(
            Namespace: @namespace,
            Pod: pod,
            Container: container ?? string.Empty,
            Logs: body,
            Truncated: truncated,
            TotalLines: lineCount,
            FetchedAt: DateTime.UtcNow,
            Citation: K8sCitations.ForLogs(@namespace, pod, container, clamped, dashboard));
    }

    /// <summary>List deployments, optionally scoped to a namespace.</summary>
    [KernelFunction("list_deployments")]
    [Description("Read-only: list deployments in a namespace, or across all namespaces when 'namespace' is omitted.")]
    public async Task<IReadOnlyList<K8sDeploymentSummary>> ListDeploymentsAsync(
        [Description("Optional namespace.")] string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var dashboard = _options.Value.DashboardBaseUrl;
        var list = string.IsNullOrWhiteSpace(@namespace)
            ? await _readOps.ListDeploymentsForAllNamespacesAsync(cancellationToken).ConfigureAwait(false)
            : await _readOps.ListDeploymentsAsync(@namespace, cancellationToken).ConfigureAwait(false);

        if (list?.Items is null)
        {
            return Array.Empty<K8sDeploymentSummary>();
        }

        return list.Items.Select(d => new K8sDeploymentSummary(
            Namespace: d.Metadata?.NamespaceProperty ?? string.Empty,
            Name: d.Metadata?.Name ?? string.Empty,
            Replicas: d.Spec?.Replicas ?? 0,
            ReadyReplicas: d.Status?.ReadyReplicas ?? 0,
            UpdatedReplicas: d.Status?.UpdatedReplicas ?? 0,
            AvailableReplicas: d.Status?.AvailableReplicas ?? 0,
            Strategy: d.Spec?.Strategy?.Type,
            Citation: K8sCitations.ForDeployment(
                d.Metadata?.NamespaceProperty ?? string.Empty,
                d.Metadata?.Name ?? string.Empty,
                dashboard))).ToArray();
    }

    /// <summary>List recent events, optionally scoped and time-bound.</summary>
    [KernelFunction("list_events")]
    [Description("Read-only: list recent events, optionally scoped to a namespace and time window in seconds.")]
    public async Task<IReadOnlyList<K8sEvent>> ListEventsAsync(
        [Description("Optional namespace.")] string? @namespace = null,
        [Description("Only return events with lastTimestamp newer than this many seconds.")] int? sinceSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        var list = string.IsNullOrWhiteSpace(@namespace)
            ? await _readOps.ListEventsForAllNamespacesAsync(cancellationToken).ConfigureAwait(false)
            : await _readOps.ListEventsAsync(@namespace, cancellationToken).ConfigureAwait(false);

        if (list?.Items is null)
        {
            return Array.Empty<K8sEvent>();
        }

        IEnumerable<Corev1Event> filtered = list.Items;
        if (sinceSeconds is { } since && since > 0)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-since);
            filtered = filtered.Where(e =>
            {
                var t = e.LastTimestamp ?? e.EventTime ?? DateTime.MinValue;
                return t >= cutoff;
            });
        }

        return filtered
            .OrderByDescending(e => e.LastTimestamp ?? e.EventTime ?? DateTime.MinValue)
            .Select(MapEvent)
            .ToArray();
    }

    private static K8sPodSummary MapPodSummary(V1Pod pod, string? dashboardBaseUrl)
    {
        var ns = pod.Metadata?.NamespaceProperty ?? string.Empty;
        var name = pod.Metadata?.Name ?? string.Empty;
        var containerStatuses = pod.Status?.ContainerStatuses ?? new List<V1ContainerStatus>();
        var owner = pod.Metadata?.OwnerReferences?.FirstOrDefault(o => o.Controller == true)
                    ?? pod.Metadata?.OwnerReferences?.FirstOrDefault();

        return new K8sPodSummary(
            Namespace: ns,
            Name: name,
            Phase: pod.Status?.Phase ?? "Unknown",
            Restarts: containerStatuses.Sum(c => c.RestartCount),
            ContainerCount: pod.Spec?.Containers?.Count ?? containerStatuses.Count,
            ReadyContainers: containerStatuses.Count(c => c.Ready),
            Node: pod.Spec?.NodeName ?? string.Empty,
            StartedAt: pod.Status?.StartTime ?? pod.Metadata?.CreationTimestamp ?? DateTime.MinValue,
            OwnerKind: owner?.Kind,
            OwnerName: owner?.Name,
            Citation: K8sCitations.ForPod(ns, name, dashboardBaseUrl));
    }

    private static IReadOnlyList<K8sContainerStatus> MapContainerStatuses(V1Pod pod)
    {
        var statuses = pod.Status?.ContainerStatuses;
        if (statuses is null)
        {
            return Array.Empty<K8sContainerStatus>();
        }

        return statuses.Select(s =>
        {
            var (state, terminated) = DescribeState(s.State);
            var lastTerminated = s.LastState?.Terminated;
            return new K8sContainerStatus(
                Name: s.Name ?? string.Empty,
                Image: s.Image ?? string.Empty,
                State: state,
                Restarts: s.RestartCount,
                LastTerminationReason: terminated?.Reason ?? lastTerminated?.Reason,
                ExitCode: terminated?.ExitCode ?? lastTerminated?.ExitCode);
        }).ToArray();
    }

    private static (string State, V1ContainerStateTerminated? Terminated) DescribeState(V1ContainerState? state)
    {
        if (state is null)
        {
            return ("Unknown", null);
        }
        if (state.Running is not null)
        {
            return ("Running", null);
        }
        if (state.Terminated is not null)
        {
            return ("Terminated", state.Terminated);
        }
        if (state.Waiting is not null)
        {
            return ("Waiting", null);
        }
        return ("Unknown", null);
    }

    private static K8sEvent MapEvent(Corev1Event e)
        => new(
            Namespace: e.Metadata?.NamespaceProperty ?? string.Empty,
            ObjectKind: e.InvolvedObject?.Kind ?? string.Empty,
            ObjectName: e.InvolvedObject?.Name ?? string.Empty,
            Type: e.Type ?? string.Empty,
            Reason: e.Reason ?? string.Empty,
            Message: e.Message ?? string.Empty,
            Count: e.Count ?? 1,
            LastSeen: e.LastTimestamp ?? e.EventTime ?? DateTime.MinValue);
}
