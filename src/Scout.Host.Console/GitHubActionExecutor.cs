using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Llm.Abstractions;
using Scout.Plugin.GitHub;

namespace Scout.Host.Console;

/// <summary>
/// Runs, every <see cref="Interval"/>, the GitHub actions the control plane hands out, one after the other until none is
/// left or one cannot be recorded, pausing <see cref="BetweenSends"/> after each one that was sent: the oldest verdict, review, new issue, comment or reply the owner approved, claimed for
/// this run; a verdict or a review either clicked on a review card or asked
/// in chat, an issue, a comment or a reply asked in chat and run from the arguments queued with it, each approved on its card
/// or the Review screen. A review, an issue, a comment or a reply is recorded with its link, or with the sentence that says
/// why it failed. An action is handed to this executor once: the control plane never hands out a claimed action again.
/// The verdict is sent pinned to the commit reviewed and refused when the pull request moved past it; the run is recorded
/// at once with the review's link, or with the sentence that says why it failed, which consumes the action, and only once
/// recorded is the tests line posted on the pull request's conversation, a failure there being logged and never failing the
/// verdict; a record the control plane refuses, as after the owner revoked GitHub writes, posts no tests line. Just before a
/// verdict is sent the owner's grant is read once more: a grant gone, which the control plane answers by rejecting the action
/// as writes were revoked, or a read that fails, sends nothing. The record names the login that signed the verdict.
/// </summary>
internal sealed class GitHubActionExecutor(
    IApprovedActions approved,
    IWriteGate gate,
    GitHubVerdictRunner verdicts,
    GitHubWriteRunner writes,
    TimeProvider time,
    ILogger<GitHubActionExecutor> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The pause after an action was sent to GitHub, before the next one is taken: GitHub asks that requests which create
    /// content be made one at a time, at least a second apart, and answers a burst with a secondary rate limit.
    /// </summary>
    internal static readonly TimeSpan BetweenSends = TimeSpan.FromSeconds(1);

    internal const string FeatureId = "github";
    internal const string VerdictScope = "Pull request verdicts";
    internal const string ReviewScope = "Pull request reviews";

    private bool _pollFailureLogged;

    internal const string Unexpected = "The verdict failed in Scout before GitHub answered; give it again from the review card.";

    internal const string WriteUnexpected = "The write failed in Scout before GitHub answered; ask for it again in chat.";

    private const string RefusedOutcome = "failed: refused";

    internal static string Unsupported(string scope) => $"Scout does not run {scope} on GitHub.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                while (await RunOnceAsync(stoppingToken).ConfigureAwait(false) is var ran && ran != Ran.Nothing)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    if (ran == Ran.Sent)
                    {
                        await Task.Delay(BetweenSends, time, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>What one run did with the oldest approved GitHub action.</summary>
    internal enum Ran
    {
        /// <summary>No action was taken, or the one taken was not recorded: nothing more runs until the next tick.</summary>
        Nothing,

        /// <summary>An action was recorded without a request leaving for GitHub: refused, unsupported or unreadable.</summary>
        Refused,

        /// <summary>An action was sent to GitHub and recorded, whatever GitHub answered.</summary>
        Sent,
    }

    /// <summary>Run the oldest approved GitHub action, if one waits, and say whether it was recorded and whether it was sent.</summary>
    internal async Task<Ran> RunOnceAsync(CancellationToken ct)
    {
        ApprovedAction? action;
        try
        {
            action = await approved.ClaimNextAsync(FeatureId, ct).ConfigureAwait(false);
            _pollFailureLogged = false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!_pollFailureLogged)
            {
                logger.LogWarning("Scout cannot read the approved GitHub actions ({ExceptionType}).", ex.GetType().Name);
                _pollFailureLogged = true;
            }

            return Ran.Nothing;
        }

        if (action is null)
        {
            return Ran.Nothing;
        }

        GivenVerdict? given = null;
        string? signer = null;
        string outcome;
        string result;
        var isVerdict = action.Scope == VerdictScope;
        var isReview = action.Scope == ReviewScope;
        if (!isVerdict && !isReview && !GitHubWriteRunner.Runs(action.Target))
        {
            (outcome, result) = (RefusedOutcome, Unsupported(action.Scope));
        }
        else
        {
            bool granted;
            try
            {
                granted = await approved.CheckGrantAsync(action, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Scout could not read GitHub's write permission before sending action {ActionId} ({ExceptionType}), so it sent nothing.",
                    action.Id,
                    ex.GetType().Name);
                return Ran.Nothing;
            }

            if (!granted)
            {
                logger.LogInformation("GitHub action {ActionId} was not sent: GitHub writes were revoked.", action.Id);
                return Ran.Nothing;
            }

            try
            {
                if (isVerdict)
                {
                    given = await verdicts.GiveAsync(action.Payload, ct).ConfigureAwait(false);
                    (outcome, result, signer) = ("succeeded", given.Url, given.Signer);
                }
                else
                {
                    var done = isReview
                        ? await writes.PostReviewAsync(action.Payload, ct).ConfigureAwait(false)
                        : await writes.RunAsync(action.Target, action.Payload, ct).ConfigureAwait(false);
                    (outcome, result, signer) = ("succeeded", done.Url, done.Signer);
                }
            }
            catch (WriteFailedException ex)
            {
                (outcome, result) = ($"failed: {ex.Outcome}", ex.Message);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                (outcome, result) = ($"failed: {ex.GetType().Name}", isVerdict ? Unexpected : WriteUnexpected);
            }
        }

        try
        {
            await gate.RecordAsync(FeatureId, action.Scope, action.Target, outcome, action.Id, result, signer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "GitHub action {ActionId} ran ({Outcome}) but could not be recorded ({ExceptionType}).", action.Id, outcome, ex.GetType().Name);
            return Ran.Nothing;
        }

        var ran = outcome == RefusedOutcome ? Ran.Refused : Ran.Sent;
        if (given is null)
        {
            return ran;
        }

        try
        {
            await verdicts.PostTestsLineAsync(given, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "The verdict of GitHub action {ActionId} was given, but its tests line was not posted ({ExceptionType}).", action.Id, ex.GetType().Name);
        }

        return ran;
    }
}
