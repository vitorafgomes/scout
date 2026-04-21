using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Plugin.CloudStatus.Domain;
using Scout.Plugin.CloudStatus.Providers.Aws;

namespace Scout.Plugin.CloudStatus.Providers;

/// <summary>
/// Read-only AWS cloud health provider. Queries AWS Health via
/// <see cref="IAwsHealthSource"/> (seam so the AWS SDK can be faked) and
/// maps open events into a <see cref="StatusReport"/>.
///
/// Operational behavior:
/// <list type="bullet">
/// <item>No open events → <see cref="HealthLevel.Healthy"/>.</item>
/// <item>
///   <c>issue</c> or <c>investigation</c> categories →
///   <see cref="HealthLevel.Unhealthy"/> (active customer impact).
/// </item>
/// <item>
///   <c>accountNotification</c> / <c>scheduledChange</c> /
///   unknown categories → <see cref="HealthLevel.Degraded"/> (informational
///   but worth surfacing).
/// </item>
/// <item>
///   AWS <c>SubscriptionRequiredException</c> (account lacks
///   Business / Enterprise AWS Support) → <see cref="HealthLevel.Unknown"/>
///   with a clear hint.
/// </item>
/// <item>
///   AWS-specific exceptions (throttling, auth, service error) →
///   <see cref="HealthLevel.Unknown"/> with the AWS error code.
/// </item>
/// <item>Any other exception → <see cref="HealthLevel.Unknown"/>.</item>
/// </list>
/// Only <see cref="OperationCanceledException"/> propagates out.
/// </summary>
public sealed class AwsCloudHealthProvider : ICloudHealthProvider
{
    public const string ProviderName = "AWS";

    private readonly IAwsHealthSource _source;
    private readonly ILogger<AwsCloudHealthProvider> _logger;
    private readonly string _scope;
    private readonly TimeProvider _timeProvider;

    public AwsCloudHealthProvider(
        IOptions<AwsCloudHealthOptions> options,
        IAwsHealthSource source,
        ILogger<AwsCloudHealthProvider> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(options.Value.Region))
        {
            throw new ArgumentException(
                "AwsCloudHealthOptions.Region must be non-empty.",
                nameof(options));
        }

        _scope = options.Value.Region;
    }

    public string Name => ProviderName;

    public async Task<StatusReport> GetHealthAsync(CancellationToken cancellationToken)
    {
        var checkedAt = _timeProvider.GetUtcNow();

        IReadOnlyList<AwsHealthEvent> events;
        try
        {
            events = await _source
                .GetOpenEventsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AmazonServiceException ex) when (
            string.Equals(ex.ErrorCode, "SubscriptionRequiredException", StringComparison.Ordinal))
        {
            _logger.LogWarning(ex,
                "AWS Health API requires a Business or Enterprise Support subscription " +
                "on the target account.");
            return StatusReport.Unknown(
                ProviderName,
                _scope,
                checkedAt,
                "AWS Health API requires Business or Enterprise Support on the " +
                "target account. Upgrade the support plan or rely on the public AWS " +
                "Health Dashboard instead.");
        }
        catch (AmazonServiceException ex)
        {
            _logger.LogWarning(ex,
                "AWS Health call failed (error code {ErrorCode}, status {StatusCode}).",
                ex.ErrorCode ?? "(none)", ex.StatusCode);
            return StatusReport.Unknown(
                ProviderName,
                _scope,
                checkedAt,
                $"AWS returned {ex.ErrorCode ?? "error"} (HTTP {(int)ex.StatusCode}).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying AWS Health.");
            return StatusReport.Unknown(
                ProviderName,
                _scope,
                checkedAt,
                ex.Message);
        }

        if (events.Count == 0)
        {
            return new StatusReport(
                Provider: ProviderName,
                Scope: _scope,
                Overall: HealthLevel.Healthy,
                Services: Array.Empty<ServiceStatus>(),
                CheckedAt: checkedAt);
        }

        var services = events
            .Select(e => new ServiceStatus(
                Service: e.Service,
                Region: e.Region,
                Level: ToHealthLevel(e.Category),
                Message: e.Description))
            .ToArray();

        var overall = services.Select(s => s.Level).Max();

        return new StatusReport(
            Provider: ProviderName,
            Scope: _scope,
            Overall: overall,
            Services: services,
            CheckedAt: checkedAt);
    }

    private static HealthLevel ToHealthLevel(string category) =>
        category.Trim().ToLowerInvariant() switch
        {
            "issue" or "investigation" => HealthLevel.Unhealthy,
            "accountnotification" or "scheduledchange" => HealthLevel.Degraded,
            _ => HealthLevel.Degraded,
        };
}
