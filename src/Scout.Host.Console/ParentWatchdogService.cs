using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scout.Host.Console;

/// <summary>
/// Stops the host when its supervising parent process dies ungracefully (crash
/// or SIGKILL). Without this, a force-killed Electron parent leaves the host
/// reparented to init and still holding the bot session. Liveness is observed
/// through an injected <see cref="IParentLivenessProbe"/> (default: stdin EOF).
/// Only registered when the supervisor sets SCOUT__PARENT_WATCHDOG=1.
/// </summary>
internal sealed class ParentWatchdogService(
    IParentLivenessProbe probe,
    IHostApplicationLifetime lifetime,
    ILogger<ParentWatchdogService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await probe.WaitForParentExitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown cancelled the wait; do not stop spuriously.
            return;
        }

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        logger.LogInformation("Parent process gone (stdin EOF); shutting down.");
        lifetime.StopApplication();
    }
}
