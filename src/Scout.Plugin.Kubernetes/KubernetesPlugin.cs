using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using k8s;
using k8s.Exceptions;
using k8s.Models;
using Microsoft.SemanticKernel;
using HttpOperationException = k8s.Autorest.HttpOperationException;

namespace Scout.Plugin.Kubernetes;

/// <summary>
/// Read-only Kubernetes plugin exposed to the LLM as Semantic Kernel functions. Every
/// function sends GET requests only, to a context the owner allowed; no logs, secrets,
/// config map data, exec, attach, port-forward, proxy or watch exist by design. A namespace
/// or pod name that is not a Kubernetes name is refused before any request, so model input
/// cannot reach another API path. Every call returns a user-safe string; failures become a
/// short message that never quotes the cluster's answer or the kubeconfig.
/// </summary>
public sealed partial class KubernetesPlugin : IDisposable
{
    private const int PodEventLimit = 20;
    private const int MaxLines = 100;
    private const int MaxEventMessage = 200;
    private const int MaxQuotedInput = 63;
    private const int MaxSinceMinutes = 1440;
    private const string ExecFailurePrefix = "external exec failed";
    private const string NarrowHint = "ask about one namespace to narrow it.";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly string _kubeconfigPath;
    private readonly (string Label, string Name)[] _contexts;
    private readonly Func<string, IKubernetes> _buildClient;
    private readonly ConcurrentDictionary<string, Lazy<IKubernetes>> _clients = new(StringComparer.Ordinal);

    static KubernetesPlugin()
    {
        KubernetesClientConfiguration.ExecTimeout = RequestTimeout;
    }

    /// <summary>Creates the plugin over the owner's kubeconfig and the contexts allowed in it.</summary>
    /// <param name="options">Kubeconfig path and allowed contexts. Must be non-null.</param>
    public KubernetesPlugin(KubernetesOptions options)
        : this(options, null)
    {
    }

    internal KubernetesPlugin(KubernetesOptions options, Func<string, IKubernetes>? buildClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        _buildClient = buildClient ?? BuildClient;
        _kubeconfigPath = options.KubeconfigPath;
        _contexts = options.Contexts
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => (Label: string.IsNullOrWhiteSpace(c.Label) ? c.Name.Trim() : c.Label.Trim(), Name: c.Name.Trim()))
            .DistinctBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_contexts.Length == 0)
        {
            throw new ArgumentException("At least one allowed context is required.", nameof(options));
        }
    }

    /// <summary>List the labels of the contexts Scout may read.</summary>
    /// <returns>One label per line; the first is the default.</returns>
    [KernelFunction("list_contexts")]
    [Description("List the labels of the Kubernetes contexts Scout may read, one per line. The first one is used when a function is called without a context.")]
    public string ListContexts()
        => string.Join("\n", _contexts.Select(c => c.Label));

    /// <summary>List the namespaces of a cluster.</summary>
    /// <param name="context">An allowed context label; omitted means the default.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>One namespace per line with its phase, or a clean message.</returns>
    [KernelFunction("list_namespaces")]
    [Description("List the namespaces of a Kubernetes cluster.")]
    public Task<string> ListNamespacesAsync(
        [Description("Context label from list_contexts. Omit for the default context.")] string? context = null,
        CancellationToken ct = default)
        => RunAsync(context, "namespaces", ct, async client =>
        {
            var list = await client.CoreV1.ListNamespaceAsync(limit: MaxLines, cancellationToken: ct).ConfigureAwait(false);
            return list.Items.Count == 0
                ? "No namespaces."
                : Paged(list.Items.Select(n => $"{n.Metadata?.Name} ({n.Status?.Phase ?? "Unknown"})"), list.Metadata);
        });

    /// <summary>List pods with phase, readiness, restarts and the reason a container is waiting.</summary>
    /// <param name="namespace">Namespace; omitted means all namespaces.</param>
    /// <param name="context">An allowed context label; omitted means the default.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>One pod per line, or a clean message.</returns>
    [KernelFunction("list_pods")]
    [Description("List pods with phase, ready containers, restarts and why a container is waiting or stopped (for example CrashLoopBackOff or ImagePullBackOff).")]
    public Task<string> ListPodsAsync(
        [Description("Namespace. Omit to list pods in all namespaces.")] string? @namespace = null,
        [Description("Context label from list_contexts. Omit for the default context.")] string? context = null,
        CancellationToken ct = default)
        => InvalidOptionalNamespace(@namespace) is { } refusal
            ? Task.FromResult(refusal)
            : RunAsync(context, "pods", ct, async client =>
            {
                var list = string.IsNullOrWhiteSpace(@namespace)
                    ? await client.CoreV1.ListPodForAllNamespacesAsync(limit: MaxLines, cancellationToken: ct).ConfigureAwait(false)
                    : await client.CoreV1.ListNamespacedPodAsync(@namespace, limit: MaxLines, cancellationToken: ct).ConfigureAwait(false);
                return list.Items.Count == 0
                    ? "No pods."
                    : Paged(list.Items.Select(FormatPod), list.Metadata);
            });

    /// <summary>Describe one pod: its containers and the last events about it.</summary>
    /// <param name="namespace">The pod's namespace.</param>
    /// <param name="name">The pod's name.</param>
    /// <param name="context">An allowed context label; omitted means the default.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>A pod summary, one line per container and up to 20 events, newest first.</returns>
    [KernelFunction("describe_pod")]
    [Description("Describe one pod: each container's state, readiness, restarts and last termination, and the last 20 events about the pod.")]
    public Task<string> DescribePodAsync(
        [Description("The pod's namespace.")] string @namespace,
        [Description("The pod's name.")] string name,
        [Description("Context label from list_contexts. Omit for the default context.")] string? context = null,
        CancellationToken ct = default)
        => (Refusal(@namespace, "namespace", NamespacePattern(), 63) ?? Refusal(name, "pod name", PodNamePattern(), 253)) is { } refusal
            ? Task.FromResult(refusal)
            : RunAsync(context, $"pod {@namespace}/{name}", ct, async client =>
            {
                var pod = await client.CoreV1.ReadNamespacedPodAsync(name, @namespace, cancellationToken: ct).ConfigureAwait(false);
                var events = await client.CoreV1.ListNamespacedEventAsync(
                    @namespace,
                    fieldSelector: $"involvedObject.kind=Pod,involvedObject.name={name}",
                    cancellationToken: ct).ConfigureAwait(false);

                var lines = new List<string> { FormatPod(pod) };
                lines.AddRange((pod.Status?.ContainerStatuses ?? []).Select(FormatContainer));
                var recent = events.Items.OrderByDescending(EventTime).Take(PodEventLimit).ToList();
                lines.Add(recent.Count == 0 ? "No recent events." : "Events, newest first:");
                lines.AddRange(recent.Select(FormatEvent));
                return string.Join("\n", lines);
            });

    /// <summary>List deployments with ready and desired replicas.</summary>
    /// <param name="namespace">Namespace; omitted means all namespaces.</param>
    /// <param name="context">An allowed context label; omitted means the default.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>One deployment per line, or a clean message.</returns>
    [KernelFunction("list_deployments")]
    [Description("List deployments with ready and desired replicas.")]
    public Task<string> ListDeploymentsAsync(
        [Description("Namespace. Omit to list deployments in all namespaces.")] string? @namespace = null,
        [Description("Context label from list_contexts. Omit for the default context.")] string? context = null,
        CancellationToken ct = default)
        => InvalidOptionalNamespace(@namespace) is { } refusal
            ? Task.FromResult(refusal)
            : RunAsync(context, "deployments", ct, async client =>
            {
                var list = string.IsNullOrWhiteSpace(@namespace)
                    ? await client.AppsV1.ListDeploymentForAllNamespacesAsync(limit: MaxLines, cancellationToken: ct).ConfigureAwait(false)
                    : await client.AppsV1.ListNamespacedDeploymentAsync(@namespace, limit: MaxLines, cancellationToken: ct).ConfigureAwait(false);
                return list.Items.Count == 0
                    ? "No deployments."
                    : Paged(
                        list.Items.Select(d =>
                            $"{d.Metadata?.NamespaceProperty}/{d.Metadata?.Name} ready {d.Status?.ReadyReplicas ?? 0}/{d.Spec?.Replicas ?? 0}"),
                        list.Metadata);
            });

    /// <summary>List recent events, warnings first.</summary>
    /// <param name="namespace">Namespace; omitted means all namespaces.</param>
    /// <param name="sinceMinutes">Only events seen in this many minutes, from 1 to 1440.</param>
    /// <param name="context">An allowed context label; omitted means the default.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Warnings first, then normal events, each newest first; or a clean message.</returns>
    [KernelFunction("list_events")]
    [Description("List recent cluster events, warnings first, newest first within each group.")]
    public Task<string> ListEventsAsync(
        [Description("Namespace. Omit to list events in all namespaces.")] string? @namespace = null,
        [Description("Only events seen in the last this many minutes, from 1 to 1440.")] int sinceMinutes = 10,
        [Description("Context label from list_contexts. Omit for the default context.")] string? context = null,
        CancellationToken ct = default)
        => InvalidOptionalNamespace(@namespace) is { } refusal
            ? Task.FromResult(refusal)
            : RunAsync(context, "events", ct, async client =>
            {
                // ponytail: every event the server still keeps (its TTL, one hour by default) is fetched and filtered here; page with limit and continue if a cluster's event volume bites.
                var list = string.IsNullOrWhiteSpace(@namespace)
                    ? await client.CoreV1.ListEventForAllNamespacesAsync(cancellationToken: ct).ConfigureAwait(false)
                    : await client.CoreV1.ListNamespacedEventAsync(@namespace, cancellationToken: ct).ConfigureAwait(false);
                var minutes = Math.Clamp(sinceMinutes, 1, MaxSinceMinutes);
                var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
                var recent = list.Items
                    .Where(e => EventTime(e) >= cutoff)
                    .OrderByDescending(e => e.Type == "Warning")
                    .ThenByDescending(EventTime)
                    .ToList();
                return recent.Count == 0
                    ? $"No events in the last {minutes} minutes."
                    : Capped(recent.Select(FormatEvent));
            });

    /// <summary>Disposes every client built for a context.</summary>
    public void Dispose()
    {
        foreach (var client in _clients.Values.Where(c => c.IsValueCreated))
        {
            client.Value.Dispose();
        }

        _clients.Clear();
    }

    [GeneratedRegex(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();

    [GeneratedRegex(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PodNamePattern();

    private static string? InvalidOptionalNamespace(string? @namespace)
        => string.IsNullOrWhiteSpace(@namespace) ? null : Refusal(@namespace, "namespace", NamespacePattern(), 63);

    private static string? Refusal(string? value, string kind, Regex pattern, int maxLength)
    {
        value ??= string.Empty;
        if (value.Length <= maxLength && pattern.IsMatch(value))
        {
            return null;
        }

        var quoted = value.Length > MaxQuotedInput ? value[..MaxQuotedInput] : value;
        return $"'{quoted}' is not a valid Kubernetes {kind}.";
    }

    private async Task<string> RunAsync(
        string? requested,
        string what,
        CancellationToken ct,
        Func<IKubernetes, Task<string>> read)
    {
        var match = string.IsNullOrWhiteSpace(requested)
            ? _contexts[0]
            : _contexts.FirstOrDefault(c => string.Equals(c.Label, requested.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match.Name is null)
        {
            return $"That is not a context Scout may read. Allowed contexts: {string.Join(", ", _contexts.Select(c => c.Label))}.";
        }

        var context = match.Label;
        IKubernetes client;
        try
        {
            client = GetClient(match.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConfigFailure(ex, context);
        }

        try
        {
            return await read(client).ConfigureAwait(false);
        }
        catch (KubeConfigException ex)
        {
            return ConfigFailure(ex, context);
        }
        catch (HttpOperationException ex)
        {
            return ex.Response?.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    => $"The credentials for context {context} cannot read {what}.",
                HttpStatusCode.NotFound => $"Not found in context {context}: {what}.",
                null => $"The cluster for context {context} could not be read.",
                var code => $"The cluster for context {context} answered {(int?)code} when reading {what}.",
            };
        }
        catch (HttpRequestException)
        {
            return $"Could not reach the cluster for context {context}.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"The cluster for context {context} did not answer within {RequestTimeout.TotalSeconds} seconds.";
        }
    }

    private IKubernetes GetClient(string context)
    {
        var lazy = _clients.GetOrAdd(
            context,
            c => new Lazy<IKubernetes>(() => _buildClient(c), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _clients.TryRemove(new KeyValuePair<string, Lazy<IKubernetes>>(context, lazy));
            throw;
        }
    }

    private IKubernetes BuildClient(string context)
    {
        // ponytail: KubernetesClient runs an exec credential command once here and again on the client's first request (ExecTokenProvider starts empty, without a lock); wrap the token provider if double runs matter.
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(_kubeconfigPath, context);
        config.HttpClientTimeout = RequestTimeout;
        return new k8s.Kubernetes(config);
    }

    private static string Paged(IEnumerable<string> lines, V1ListMeta? meta)
    {
        if (meta?.RemainingItemCount is > 0 and var remaining)
        {
            return string.Join("\n", lines.Append($"...and {remaining} more; {NarrowHint}"));
        }

        return string.IsNullOrEmpty(meta?.ContinueProperty)
            ? Capped(lines)
            : string.Join("\n", lines.Append($"...and more; {NarrowHint}"));
    }

    private static string Capped(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        return all.Count <= MaxLines
            ? string.Join("\n", all)
            : string.Join("\n", all.Take(MaxLines).Append($"...and {all.Count - MaxLines} more; {NarrowHint}"));
    }

    private static string ConfigFailure(Exception ex, string context)
        => ex is KubeConfigException && ex.Message.StartsWith(ExecFailurePrefix, StringComparison.Ordinal)
            ? $"The command that gets the token for context {context} failed."
            : $"The Kubernetes configuration for context {context} could not be read.";

    private static string FormatPod(V1Pod pod)
    {
        var statuses = pod.Status?.ContainerStatuses ?? [];
        var total = pod.Spec?.Containers?.Count ?? statuses.Count;
        var line = $"{pod.Metadata?.NamespaceProperty}/{pod.Metadata?.Name} {pod.Status?.Phase ?? "Unknown"}, " +
                   $"ready {statuses.Count(s => s.Ready)}/{total}, restarts {statuses.Sum(s => s.RestartCount)}";
        var reason = statuses
            .Select(s => s.State?.Waiting?.Reason ?? s.State?.Terminated?.Reason)
            .FirstOrDefault(r => !string.IsNullOrEmpty(r));
        return reason is null ? line : $"{line}, {reason}";
    }

    private static string FormatContainer(V1ContainerStatus s)
    {
        var state = s.State?.Running is not null ? "Running"
            : s.State?.Waiting is { } waiting ? $"Waiting ({waiting.Reason})"
            : s.State?.Terminated is { } terminated ? $"Terminated ({terminated.Reason}, exit {terminated.ExitCode})"
            : "Unknown";
        var line = $"Container {s.Name} ({s.Image}): {state}, ready {(s.Ready ? "yes" : "no")}, restarts {s.RestartCount}";
        return s.LastState?.Terminated is { } last
            ? $"{line}, last terminated {last.Reason} (exit {last.ExitCode})"
            : line;
    }

    private static DateTime EventTime(Corev1Event e)
        => e.LastTimestamp ?? e.EventTime ?? e.Metadata?.CreationTimestamp ?? DateTime.MinValue;

    private static string FormatEvent(Corev1Event e)
    {
        var message = e.Message is { Length: > MaxEventMessage } longMessage
            ? $"{longMessage[..MaxEventMessage]}..."
            : e.Message;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{EventTime(e):yyyy-MM-dd HH:mm:ss} {e.Type} {e.Metadata?.NamespaceProperty}/{e.InvolvedObject?.Kind}/{e.InvolvedObject?.Name} {e.Reason}: {message}");
    }
}
