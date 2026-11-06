using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// Runs, every <see cref="Interval"/>, the GitHub action the control plane hands out: the oldest one the owner approved,
/// claimed for this run. It posts the draft the action belongs to, a review or an issue, records the write against
/// the action, deletes the draft and tells the chat the request came from. An action without a draft (purged, or
/// written by another worker) is recorded failed, so the queue moves on. A failure GitHub may recover from (a
/// transport fault, a timeout, a 5xx, a 401 while the token file is rewritten, a 403 rate limit, a 429) leaves the
/// action claimed; the claim lapses and a later run posts it. Any other GitHub refusal is recorded failed with its
/// reason and the draft is deleted. Every run is safe to repeat: the draft is marked posting before the POST, a
/// result this process already holds is recorded without a second POST, and a draft found posting by a process that
/// knows nothing of it is recorded failed as possibly posted rather than posted again. Any other exception is logged
/// and leaves the action for the next run; nothing here stops the host. Drafts older than a week are purged on every run.
/// </summary>
internal sealed class GitHubActionExecutor(
    IApprovedActions approved,
    IGitHubWriteClient github,
    IWriteGate gate,
    GitHubDraftStore drafts,
    IOriginNotifier notifier,
    TimeProvider time,
    ILogger<GitHubActionExecutor> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    internal const string DraftMissing = "failed: the draft is no longer on this computer";

    internal const string MayBePosted = "failed: an earlier run may have posted it";

    internal static string MayAlreadyBePosted(string target) =>
        $"The write on {target} the owner approved may already be on GitHub; an earlier run was interrupted while posting it. Check it there before asking again.";

    internal static string ReviewPosted(string target, string url) => $"Review posted on {target}: {url}";

    internal static string IssueOpened(string target, int number, string url) => $"Issue #{number} opened on {target}: {url}";

    internal static string NotPosted(string target, string reason) => $"The write on {target} the owner approved was not performed: {reason}";

    private readonly ConcurrentDictionary<string, string> _postedNotRecorded = new(StringComparer.Ordinal);
    private bool _pollFailureLogged;

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

    /// <summary>Purge stale drafts, then run the oldest approved action, if one waits.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await drafts.PurgeAsync(time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            logger.LogWarning("The GitHub drafts folder could not be read: {Reason}", ex.Message);
        }

        ApprovedAction? action;
        try
        {
            action = await approved.ClaimNextAsync(GitHubActionsPlugin.FeatureId, ct).ConfigureAwait(false);
            _pollFailureLogged = false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!_pollFailureLogged)
            {
                logger.LogWarning("Scout cannot read approved GitHub actions: {ExceptionType}", ex.GetType().Name);
                _pollFailureLogged = true;
            }

            return;
        }

        if (action is null)
        {
            return;
        }

        try
        {
            await RunAsync(action, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Running GitHub action {ActionId} failed and will be retried: {ExceptionType}", action.Id, ex.GetType().Name);
        }
    }

    private async Task RunAsync(ApprovedAction action, CancellationToken ct)
    {
        var draft = await drafts.FindByActionAsync(action.Id, ct).ConfigureAwait(false);
        if (draft is null)
        {
            await RecordAsync(action, DraftMissing, ct).ConfigureAwait(false);
            return;
        }

        if (_postedNotRecorded.TryGetValue(action.Id, out var kept))
        {
            await FinishAsync(action, draft, kept, ct).ConfigureAwait(false);
            return;
        }

        if (draft.PostingAt is not null)
        {
            await RecordAsync(action, MayBePosted, ct).ConfigureAwait(false);
            await drafts.DeleteAsync(draft.Id).ConfigureAwait(false);
            await NotifyAsync(draft, MayAlreadyBePosted(draft.Target), ct).ConfigureAwait(false);
            return;
        }

        await drafts.SaveAsync(draft with { PostingAt = time.GetUtcNow() }, ct).ConfigureAwait(false);
        string notice;
        try
        {
            notice = await PostAsync(draft, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverable(ex) && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Posting {Target} failed and will be retried: {ExceptionType}", draft.Target, ex.GetType().Name);
            await drafts.SaveAsync(draft, ct).ConfigureAwait(false);
            return;
        }
        catch (GitHubApiException ex)
        {
            await RecordAsync(action, $"failed: {ex.GetType().Name}", ct).ConfigureAwait(false);
            await drafts.DeleteAsync(draft.Id).ConfigureAwait(false);
            await NotifyAsync(draft, NotPosted(draft.Target, ex.Message), ct).ConfigureAwait(false);
            return;
        }

        _postedNotRecorded[action.Id] = notice;
        await FinishAsync(action, draft, notice, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Record the posted write, forget it, delete its draft and tell the chat. A record the control plane does not take
    /// keeps the memory and the draft, so the next run records it without a second post.
    /// </summary>
    private async Task FinishAsync(ApprovedAction action, GitHubDraft draft, string notice, CancellationToken ct)
    {
        try
        {
            await gate.RecordAsync(action.FeatureId, action.Scope, action.Target, "succeeded", action.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The write gate did not record action {ActionId} ({ExceptionType}); it is kept for the next run.", action.Id, ex.GetType().Name);
            return;
        }

        _postedNotRecorded.TryRemove(action.Id, out _);
        await drafts.DeleteAsync(draft.Id).ConfigureAwait(false);
        await NotifyAsync(draft, notice, ct).ConfigureAwait(false);
    }

    /// <summary>A failure a later run may get past: no answer, or an answer GitHub gives for a moment (5xx, 401 during a token refresh, 403 rate limit, 429).</summary>
    internal static bool IsRecoverable(Exception ex) => ex switch
    {
        HttpRequestException or OperationCanceledException => true,
        GitHubApiException { StatusCode: null } => true,
        GitHubApiException api => (int)api.StatusCode!.Value >= 500
            || api.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests,
        _ => false,
    };

    private async Task<string> PostAsync(GitHubDraft draft, CancellationToken ct)
    {
        if (draft.Kind == GitHubDraft.ReviewKind)
        {
            var review = await github.SubmitReviewAsync(draft.Owner, draft.Repo, draft.Number, draft.Body, draft.Comments, ct)
                .ConfigureAwait(false);
            return ReviewPosted(draft.Target, review.HtmlUrl);
        }

        var issue = await github.CreateIssueAsync(draft.Owner, draft.Repo, draft.Title, draft.Body, draft.Labels, ct)
            .ConfigureAwait(false);
        return IssueOpened(draft.Target, issue.Number, issue.HtmlUrl);
    }

    private async Task RecordAsync(ApprovedAction action, string outcome, CancellationToken ct)
    {
        try
        {
            await gate.RecordAsync(action.FeatureId, action.Scope, action.Target, outcome, action.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The write gate did not record action {ActionId} ({ExceptionType}).", action.Id, ex.GetType().Name);
        }
    }

    private async Task NotifyAsync(GitHubDraft draft, string text, CancellationToken ct)
    {
        if (draft.OriginReference is null)
        {
            return;
        }

        try
        {
            await notifier.PostAsync(draft.OriginReference, text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("The chat that asked for {Target} could not be told: {ExceptionType}", draft.Target, ex.GetType().Name);
        }
    }
}
