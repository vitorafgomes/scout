using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers.Oci;

namespace Scout.Plugin.CloudStatus.Providers;

/// <summary>
/// Read-only Oracle Cloud health provider. Queries the public OCI status
/// endpoint via <see cref="IOciHealthSource"/> and maps active incidents
/// into a <see cref="StatusReport"/>.
///
/// Operational behavior:
/// <list type="bullet">
/// <item>No active incidents → <see cref="HealthLevel.Healthy"/>.</item>
/// <item><c>critical</c> / <c>major</c> impact → <see cref="HealthLevel.Unhealthy"/>.</item>
/// <item><c>minor</c> or unknown impact → <see cref="HealthLevel.Degraded"/>.</item>
/// <item>HTTP failure / timeout / malformed JSON → <see cref="HealthLevel.Unknown"/>.</item>
/// </list>
/// Only <see cref="OperationCanceledException"/> propagates out.
/// </summary>
public sealed class OciCloudHealthProvider : ICloudHealthProvider
{
    public const string ProviderName = "OracleCloud";
    private const string DefaultScope = "status.oraclecloud.com";

    private readonly IOciHealthSource _source;
    private readonly ILogger<OciCloudHealthProvider> _logger;
    private readonly TimeProvider _timeProvider;

    public OciCloudHealthProvider(
        IOciHealthSource source,
        ILogger<OciCloudHealthProvider> logger,
        TimeProvider? timeProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name => ProviderName;

    public async Task<StatusReport> GetHealthAsync(CancellationToken cancellationToken)
    {
        var checkedAt = _timeProvider.GetUtcNow();

        IReadOnlyList<OciHealthIncident> incidents;
        try
        {
            incidents = await _source
                .GetActiveIncidentsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller asked to cancel. Propagate cleanly.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient per-request Timeout fired. The caller token is not
            // cancelled (the when-clause above handles that case), so this
            // is an internal timeout — report Unknown, do not propagate.
            _logger.LogWarning(ex, "Oracle Cloud status endpoint timed out.");
            return StatusReport.Unknown(
                ProviderName,
                DefaultScope,
                checkedAt,
                "Oracle status endpoint timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Oracle Cloud status endpoint unreachable (status {Status}).",
                ex.StatusCode.HasValue ? ((int)ex.StatusCode.Value).ToString() : "(network)");
            return StatusReport.Unknown(
                ProviderName,
                DefaultScope,
                checkedAt,
                ex.StatusCode.HasValue
                    ? $"Oracle status endpoint returned HTTP {(int)ex.StatusCode.Value}."
                    : "Oracle status endpoint was unreachable.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying Oracle Cloud status.");
            return StatusReport.Unknown(
                ProviderName,
                DefaultScope,
                checkedAt,
                ex.Message);
        }

        if (incidents.Count == 0)
        {
            return new StatusReport(
                Provider: ProviderName,
                Scope: DefaultScope,
                Overall: HealthLevel.Healthy,
                Services: Array.Empty<ServiceStatus>(),
                CheckedAt: checkedAt);
        }

        var services = incidents
            .Select(i => new ServiceStatus(
                Service: i.Service,
                Region: i.Region,
                Level: ToHealthLevel(i.Impact),
                Message: i.Title))
            .ToArray();

        var overall = services.Select(s => s.Level).Max();

        return new StatusReport(
            Provider: ProviderName,
            Scope: DefaultScope,
            Overall: overall,
            Services: services,
            CheckedAt: checkedAt);
    }

    private static HealthLevel ToHealthLevel(string impact) =>
        impact.Trim().ToLowerInvariant() switch
        {
            "critical" or "major" => HealthLevel.Unhealthy,
            "minor" => HealthLevel.Degraded,
            "none" or "operational" => HealthLevel.Healthy,
            _ => HealthLevel.Degraded,
        };
}
