using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scout.Operator;

/// <summary>
/// Runs the operator's projects one at a time: a project a chat started as soon as it is handed over, and every
/// <see cref="PollInterval"/> the oldest project the desktop queued. A control plane that cannot answer is logged
/// once until it answers again; a project that throws is logged and saved as failed. Stops on shutdown; a project
/// cut short by shutdown keeps the status of its last save.
/// </summary>
internal sealed class OperatorWorker(
    IOperatorControlPlane controlPlane,
    OperatorPipeline pipeline,
    Channel<QueuedProject> started,
    TimeProvider time,
    ILogger<OperatorWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollFailureLogged = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (started.Reader.TryRead(out var fromChat))
                {
                    await RunAsync(fromChat, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                QueuedProject? queued = null;
                try
                {
                    queued = await controlPlane.NextQueuedAsync(stoppingToken).ConfigureAwait(false);
                    pollFailureLogged = false;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    if (!pollFailureLogged)
                    {
                        logger.LogWarning(
                            "The operator cannot read queued video projects: {Reason}",
                            ex is InvalidOperationException ? ex.Message : ex.GetType().Name);
                        pollFailureLogged = true;
                    }
                }

                if (queued is not null)
                {
                    await RunAsync(queued, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = Task.Delay(PollInterval, time, wake.Token);
        var handedOver = started.Reader.WaitToReadAsync(wake.Token).AsTask();
        await Task.WhenAny(delay, handedOver).ConfigureAwait(false);
        await wake.CancelAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(QueuedProject queued, CancellationToken stoppingToken)
    {
        try
        {
            await pipeline.RunAsync(queued, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(
                "Video project {ProjectId} could not be run ({ExceptionType}).", queued.Project.Id, ex.GetType().Name);
            try
            {
                await pipeline.SaveFailedAsync(queued.Project.Id, OperatorPipeline.Reason(ex), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception saveFailure) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(
                    "Video project {ProjectId} could not be saved as failed ({ExceptionType}).",
                    queued.Project.Id, saveFailure.GetType().Name);
            }
        }
    }
}
