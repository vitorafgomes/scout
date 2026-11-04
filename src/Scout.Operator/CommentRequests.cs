using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scout.Operator;

/// <summary>
/// Answers the owner's comment requests within a minute: every <see cref="Interval"/> it reads the comments, and runs a
/// sync when the owner asked for one, or only the drafts when a comment waits for the draft the owner asked; a drafts run
/// reads nothing from YouTube. A drafts run runs once for the same draft requests: one it left unanswered, because the model
/// failed or wrote no usable draft, waits until the requests change, the owner asks for a sync or the scheduled sync runs, so
/// the model is not asked every minute. A sync runs once for the same owner request, even when its save fails, so a control
/// plane that cannot take the save does not cost a YouTube read and model calls every minute; the owner's next request or
/// the scheduled sync tries again. A control plane that cannot be read is logged once until it answers again, and a run that
/// fails is logged once per run.
/// </summary>
internal sealed class CommentRequests(
    IOperatorControlPlane controlPlane,
    CommentDesk desk,
    TimeProvider time,
    ILogger<CommentRequests> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private bool _readFailureLogged;
    private string? _draftsRun;
    private string? _syncRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
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

    /// <summary>Run the desk once when the owner asked for a sync or a draft.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        OperatorComments read;
        try
        {
            read = await controlPlane.GetCommentsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!_readFailureLogged)
            {
                logger.LogWarning("The operator cannot answer the comment requests: {Reason}", OperatorPipeline.Reason(ex));
                _readFailureLogged = true;
            }

            return;
        }

        _readFailureLogged = false;
        var drafts = string.Join(
            '\n', read.Comments.Where(c => c.Status == CommentDesk.DraftRequested).Select(c => $"{c.Id} {c.DecidedAt}").Order(StringComparer.Ordinal));
        try
        {
            if (read.SyncRequestedAt is { } requested && requested != _syncRun)
            {
                _syncRun = requested;
                _draftsRun = null;
                await desk.RunAsync(sync: true, ct).ConfigureAwait(false);
            }
            else if (drafts.Length > 0 && drafts != _draftsRun)
            {
                _draftsRun = drafts;
                await desk.RunAsync(sync: false, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The operator could not answer a comment request: {Reason}", OperatorPipeline.Reason(ex));
        }
    }
}
