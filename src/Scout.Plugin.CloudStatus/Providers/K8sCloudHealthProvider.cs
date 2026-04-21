using k8s.Autorest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers.Kubernetes;

namespace Scout.Plugin.CloudStatus.Providers;

/// <summary>
/// Read-only Kubernetes cluster health provider. Aggregates active issues
/// reported by <see cref="IK8sHealthSource"/> into a
/// <see cref="StatusReport"/>.
///
/// Operational behavior:
/// <list type="bullet">
/// <item>No active issues → <see cref="HealthLevel.Healthy"/>.</item>
/// <item>
///   Any <c>critical</c> issue (Node NotReady, CrashLoopBackOff,
///   ImagePullBackOff, Error) → <see cref="HealthLevel.Unhealthy"/>.
/// </item>
/// <item>
///   <c>warning</c> only (Pod Pending, Unknown) → <see cref="HealthLevel.Degraded"/>.
/// </item>
/// <item>
///   Auth failure (<c>HttpOperationException</c> with 401/403) →
///   <see cref="HealthLevel.Unknown"/> with an actionable hint
///   ("check RBAC for the service account").
/// </item>
/// <item>
///   Cluster unreachable / other HTTP failure →
///   <see cref="HealthLevel.Unknown"/> with the HTTP code.
/// </item>
/// <item>Any other exception → <see cref="HealthLevel.Unknown"/>.</item>
/// </list>
/// Only <see cref="OperationCanceledException"/> propagates out.
/// </summary>
public sealed class K8sCloudHealthProvider : ICloudHealthProvider
{
    public const string ProviderName = "Kubernetes";

    private readonly IK8sHealthSource _source;
    private readonly ILogger<K8sCloudHealthProvider> _logger;
    private readonly string _scope;
    private readonly TimeProvider _timeProvider;

    public K8sCloudHealthProvider(
        IOptions<K8sCloudHealthOptions> options,
        IK8sHealthSource source,
        ILogger<K8sCloudHealthProvider> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(options.Value.ClusterName))
        {
            throw new ArgumentException(
                "K8sCloudHealthOptions.ClusterName must be non-empty.",
                nameof(options));
        }

        _scope = options.Value.ClusterName;
    }

    public string Name => ProviderName;

    public async Task<StatusReport> GetHealthAsync(CancellationToken cancellationToken)
    {
        var checkedAt = _timeProvider.GetUtcNow();

        IReadOnlyList<K8sHealthIssue> issues;
        try
        {
            issues = await _source
                .GetActiveIssuesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode is
            System.Net.HttpStatusCode.Unauthorized or
            System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(ex,
                "Kubernetes API denied access to cluster {Cluster}.", _scope);
            return StatusReport.Unknown(
                ProviderName,
                _scope,
                checkedAt,
                "Kubernetes API denied access. Check the service account's " +
                "RBAC permissions for Node / Pod list verbs.");
        }
        catch (HttpOperationException ex)
        {
            var status = ex.Response?.StatusCode is { } code ? (int)code : 0;
            _logger.LogWarning(ex,
                "Kubernetes API call to cluster {Cluster} failed (status {Status}).",
                _scope, status);
            return StatusReport.Unknown(
                ProviderName,
                _scope,
                checkedAt,
                status > 0
                    ? $"Kubernetes API returned HTTP {status}."
                    : "Kubernetes API was unreachable.");
        }
        catch (HttpRequestException ex)
        {
            // Thrown before the request reaches the server (DNS, TLS, etc.).
            _logger.LogWarning(ex,
                "Kubernetes cluster {Cluster} unreachable.", _scope);
            return StatusReport.Unknown(
                ProviderName,
                _scope,
                checkedAt,
                "Kubernetes cluster was unreachable.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error querying Kubernetes cluster {Cluster}.", _scope);
            return StatusReport.Unknown(
                ProviderName,
                _scope,
                checkedAt,
                ex.Message);
        }

        if (issues.Count == 0)
        {
            return new StatusReport(
                Provider: ProviderName,
                Scope: _scope,
                Overall: HealthLevel.Healthy,
                Services: Array.Empty<ServiceStatus>(),
                CheckedAt: checkedAt);
        }

        var services = issues
            .Select(i => new ServiceStatus(
                Service: i.Kind,
                Region: string.IsNullOrWhiteSpace(i.Namespace) ? "(cluster)" : i.Namespace,
                Level: ToHealthLevel(i.Severity),
                Message: $"{i.Name}: {i.Reason}"))
            .ToArray();

        var overall = services.Select(s => s.Level).Max();

        return new StatusReport(
            Provider: ProviderName,
            Scope: _scope,
            Overall: overall,
            Services: services,
            CheckedAt: checkedAt);
    }

    private static HealthLevel ToHealthLevel(string severity) =>
        severity.Trim().ToLowerInvariant() switch
        {
            "critical" => HealthLevel.Unhealthy,
            "warning" => HealthLevel.Degraded,
            _ => HealthLevel.Degraded,
        };
}
