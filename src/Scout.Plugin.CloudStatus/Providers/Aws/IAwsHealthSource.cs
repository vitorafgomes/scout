namespace Scout.Plugin.CloudStatus.Providers.Aws;

/// <summary>
/// Seam over the AWS Health call. Keeps the AWS SDK behind an interface so
/// <see cref="AwsCloudHealthProvider"/> stays unit-testable without a live
/// AWS account or a paid AWS Support plan.
/// </summary>
public interface IAwsHealthSource
{
    /// <summary>
    /// Return the currently-open AWS Health events for the configured account.
    /// An empty list means AWS is healthy right now.
    /// Implementations propagate <see cref="OperationCanceledException"/> and
    /// let all other exceptions bubble up so the provider can map
    /// access-denied / throttling / service errors to meaningful
    /// <see cref="Domain.HealthLevel.Unknown"/> reports.
    /// </summary>
    Task<IReadOnlyList<AwsHealthEvent>> GetOpenEventsAsync(CancellationToken cancellationToken);
}
