using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers.Azure;

namespace Scout.Plugin.CloudStatus.Providers;

/// <summary>
/// Read-only Azure cloud health provider. Queries the Azure Service Health
/// API via <see cref="IAzureHealthSource"/> (so the ARM SDK can be swapped
/// for a fake in tests) and maps active events to a <see cref="StatusReport"/>.
///
/// Operational behavior:
/// <list type="bullet">
/// <item>No active events → <see cref="HealthLevel.Healthy"/>.</item>
/// <item>Critical / Error events → <see cref="HealthLevel.Unhealthy"/>.</item>
/// <item>Warning / Informational events only → <see cref="HealthLevel.Degraded"/>.</item>
/// <item>Auth failure (403) → <see cref="HealthLevel.Unknown"/> with a hint message.</item>
/// <item>Transient failure / timeout → <see cref="HealthLevel.Unknown"/>.</item>
/// </list>
/// Exceptions never propagate out of <see cref="GetHealthAsync"/> except for
/// <see cref="OperationCanceledException"/> — the aggregate caller treats a
/// single misbehaving provider as <see cref="HealthLevel.Unknown"/>, so
/// propagating would only duplicate that safety net.
/// </summary>
public sealed class AzureCloudHealthProvider : ICloudHealthProvider
{
    public const string ProviderName = "Azure";

    private readonly IAzureHealthSource _source;
    private readonly ILogger<AzureCloudHealthProvider> _logger;
    private readonly string _subscriptionId;
    private readonly TimeProvider _timeProvider;

    public AzureCloudHealthProvider(
        IOptions<AzureCloudHealthOptions> options,
        IAzureHealthSource source,
        ILogger<AzureCloudHealthProvider> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(options.Value.SubscriptionId))
        {
            throw new ArgumentException(
                "AzureCloudHealthOptions.SubscriptionId must be non-empty.",
                nameof(options));
        }

        _subscriptionId = options.Value.SubscriptionId;
    }

    public string Name => ProviderName;

    public async Task<StatusReport> GetHealthAsync(CancellationToken cancellationToken)
    {
        var checkedAt = _timeProvider.GetUtcNow();

        IReadOnlyList<AzureHealthEvent> events;
        try
        {
            events = await _source
                .GetActiveEventsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            _logger.LogWarning(ex,
                "Azure Service Health denied access for subscription {SubscriptionId}. " +
                "Ensure the credential has the 'Reader' role at subscription scope.",
                _subscriptionId);
            return StatusReport.Unknown(
                ProviderName,
                _subscriptionId,
                checkedAt,
                "Access denied. Grant the credential at least 'Reader' on the subscription.");
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex,
                "Azure Service Health call failed for subscription {SubscriptionId} (status {Status}).",
                _subscriptionId, ex.Status);
            return StatusReport.Unknown(
                ProviderName,
                _subscriptionId,
                checkedAt,
                $"Azure returned HTTP {ex.Status}. Reported: {ex.ErrorCode ?? "no error code"}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error querying Azure Service Health for subscription {SubscriptionId}.",
                _subscriptionId);
            return StatusReport.Unknown(
                ProviderName,
                _subscriptionId,
                checkedAt,
                ex.Message);
        }

        if (events.Count == 0)
        {
            return new StatusReport(
                Provider: ProviderName,
                Scope: _subscriptionId,
                Overall: HealthLevel.Healthy,
                Services: Array.Empty<ServiceStatus>(),
                CheckedAt: checkedAt);
        }

        var services = events
            .Select(e => new ServiceStatus(
                Service: e.ImpactedService,
                Region: e.Region,
                Level: ToHealthLevel(e.Level),
                Message: e.Title))
            .ToArray();

        var overall = services.Select(s => s.Level).Max();

        return new StatusReport(
            Provider: ProviderName,
            Scope: _subscriptionId,
            Overall: overall,
            Services: services,
            CheckedAt: checkedAt);
    }

    private static HealthLevel ToHealthLevel(string level) =>
        level.Trim().ToLowerInvariant() switch
        {
            "critical" or "error" => HealthLevel.Unhealthy,
            "warning" => HealthLevel.Degraded,
            "informational" or "info" => HealthLevel.Degraded,
            "healthy" => HealthLevel.Healthy,
            _ => HealthLevel.Degraded,
        };
}
