using Scout.ControlPlane.Operator;

namespace Scout.Host.ControlPlane.Services;

/// <summary>
/// Clears the topic candidates and the comments older than 30 days through <see cref="OperatorService.ClearOldResearchAsync"/> when the
/// control plane starts and every <see cref="Interval"/> after. A run that cannot read or write Scout's data folder is
/// logged as a warning, and the next run tries again.
/// </summary>
internal sealed class ResearchCleanupService(OperatorService @operator, ILogger<ResearchCleanupService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Clear the old research once.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await @operator.ClearOldResearchAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperatorStoreException)
        {
            logger.LogWarning("Old research could not be cleared; the next run tries again: {Reason}", ex.Message);
        }
    }
}
