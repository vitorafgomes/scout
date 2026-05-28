using Scout.Plugin.CloudStatus.Domain;

namespace Scout.Plugin.CloudStatus.Providers;

/// <summary>
/// Contract every cloud health backend implements. Read-only: the contract
/// has no write methods; implementations must call only Get* / Describe*
/// operations on the underlying SDK.
///
/// When the provider cannot produce a real report (missing credentials,
/// network failure, access denied), return <see cref="StatusReport.Unknown"/>
/// with a descriptive <c>Message</c> rather than throwing. Errors that
/// should fail fast at host startup (configuration error) belong in the
/// constructor.
/// </summary>
public interface ICloudHealthProvider
{
    /// <summary>Provider identifier, e.g. "Azure", "AWS", "OracleCloud".</summary>
    string Name { get; }

    Task<StatusReport> GetHealthAsync(CancellationToken cancellationToken);
}
