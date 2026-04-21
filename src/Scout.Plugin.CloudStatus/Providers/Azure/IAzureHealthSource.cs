namespace Scout.Plugin.CloudStatus.Providers.Azure;

/// <summary>
/// Seam over the Azure Service Health call. Keeping the ARM SDK behind an
/// interface lets <see cref="AzureCloudHealthProvider"/> stay unit-testable
/// without Azure.Core.TestFramework or a live subscription.
/// </summary>
public interface IAzureHealthSource
{
    /// <summary>
    /// Return the currently-active Service Health events for the configured
    /// subscription. An empty list means the subscription is healthy right now.
    /// Implementations must propagate <see cref="OperationCanceledException"/>
    /// and let all other exceptions bubble up so the provider can map them
    /// to <see cref="Domain.HealthLevel.Unknown"/>.
    /// </summary>
    Task<IReadOnlyList<AzureHealthEvent>> GetActiveEventsAsync(CancellationToken cancellationToken);
}
