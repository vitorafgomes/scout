using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Plugin.GitHub;

/// <summary>
/// <c>start_pull_request_review</c> and <c>get_pull_request_review_status</c>: the review of a pull request too large for
/// one turn, run by the host as a job in batches. The start reads the pull request, its head commit's checks and the issue
/// its description names, and hands them to the host's reviews; the job writes into Scout's data folder only, and nothing
/// is posted on GitHub.
/// </summary>
internal sealed partial class GitHubActionsPlugin
{
    internal const string StartReview = "start_pull_request_review";
    internal const string ReviewStatus = "get_pull_request_review_status";
    internal const string NoReviews = "Review jobs are not available in this Scout.";
    internal const string InvalidPullRequestNumber = "The pull request number must be positive.";

    /// <summary>Start the review of a pull request too large for one turn as a job in batches; a write into Scout's data folder.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="ct">Cancels the reads and the start.</param>
    /// <param name="again">True when the user asked to review the pull request again.</param>
    /// <returns>The review's plan, or why it did not start.</returns>
    [KernelFunction(StartReview)]
    [Description("Start the review of a pull request whose diffs did not all fit get_pull_request's answer, as a job that runs in the background: Scout takes a snapshot at the head commit, reads every changed file's diff in batches and posts one card with the findings in this chat when the batches end. Only when get_pull_request said some diffs were left out. Nothing is posted on GitHub. Answers at once with the plan.")]
    public async Task<string> StartPullRequestReviewAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number, e.g. 42.")] int number,
        CancellationToken ct,
        [Description("True only when the user's words ask to review the pull request again, re-review it or run the review once more; a review already done at the same head commit is then run again as a new review. False otherwise.")] bool again = false)
    {
        if (Refusal(owner, repo, number) is { } refusal)
        {
            return refusal;
        }

        PullRequestDetails? pr;
        try
        {
            pr = await reads.GetPullRequestAsync(owner, repo, number, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            return $"GitHub could not be read ({ex.GetType().Name}); no review was started.";
        }

        if (pr is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Pull request #{number} not found in {owner}/{repo} or not accessible; no review was started.");
        }

        var checks = await CheckCountsAsync(owner, repo, pr.HeadSha, ct).ConfigureAwait(false);
        var purpose = await PurposeAsync(owner, repo, pr, ct).ConfigureAwait(false);
        return await reviews!.StartAsync(
            owner, repo, number, pr.HeadSha, pr.Files, GitHubPlugin.FitPatchBudget(pr.Files).LeftOut, pr.ChangedFileCount - pr.Files.Count, checks, ct, purpose, again).ConfigureAwait(false);
    }

    [GeneratedRegex(@"(?i)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+#(\d{1,9})\b")]
    private static partial Regex ClosingIssue();

    [GeneratedRegex(@"(?<![\w/])#(\d{1,9})\b")]
    private static partial Regex NamedIssue();

    /// <summary>
    /// What <paramref name="pr"/> says it does: its title and description, and the issue its description names, by a closing
    /// keyword first and else the first <c>#n</c>, never the pull request's own number, read once. An issue GitHub fails or
    /// does not find is marked unread; a number that is a pull request is no issue. Never throws for a failed read.
    /// </summary>
    private async Task<PullRequestPurpose> PurposeAsync(string owner, string repo, PullRequestDetails pr, CancellationToken ct)
    {
        var named = pr.Body is null
            ? null
            : ClosingIssue().Matches(pr.Body).Concat(NamedIssue().Matches(pr.Body))
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Where(n => n > 0 && n != pr.Number)
                .Cast<int?>()
                .FirstOrDefault();
        if (named is not { } issue)
        {
            return new(pr.Title, pr.Body, null, null, null, false);
        }

        IssueDetails? read;
        try
        {
            read = await reads.GetIssueAsync(owner, repo, issue, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            read = null;
        }

        return read switch
        {
            null => new(pr.Title, pr.Body, issue, null, null, IssueUnread: true),
            { IsPullRequest: true } => new(pr.Title, pr.Body, null, null, null, false),
            _ => new(pr.Title, pr.Body, issue, read.Title, read.Body, false),
        };
    }

    /// <summary>How the latest review job of the pull request is going.</summary>
    /// <param name="owner">Repository owner or organization login.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="number">Pull request number.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The review's progress, or that there is none.</returns>
    [KernelFunction(ReviewStatus)]
    [Description("Say how the latest review job of a pull request is going: the batches done, the files read, the findings so far. Reads Scout's own records; nothing is posted on GitHub.")]
    public Task<string> GetPullRequestReviewStatusAsync(
        [Description("Repository owner or organization login.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Pull request number, e.g. 42.")] int number,
        CancellationToken ct) =>
        Refusal(owner, repo, number) is { } refusal ? Task.FromResult(refusal) : reviews!.StatusAsync(owner, repo, number, ct);

    private string? Refusal(string owner, string repo, int number) =>
        !GitHubNames.IsValidRepository(owner, repo) ? GitHubNames.InvalidRepository
        : number <= 0 ? InvalidPullRequestNumber
        : reviews is null ? NoReviews
        : null;

    /// <summary>
    /// The checks at <paramref name="sha"/> as counts, by the rule an approval uses: completed and success, neutral or skipped
    /// passes, not completed is pending, anything else failed; null when the read fails or GitHub has more than Scout read.
    /// </summary>
    private async Task<PullRequestCheckCounts?> CheckCountsAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        try
        {
            if (await ReadChecksAsync(owner, repo, sha, ct).ConfigureAwait(false) is not { } checks)
            {
                return null;
            }

            var pending = checks.Count(c => c.Status != "completed");
            var passing = checks.Count(c => c.Status == "completed" && c.Conclusion is ("success" or "neutral" or "skipped"));
            return new PullRequestCheckCounts(passing, pending, checks.Count - pending - passing);
        }
        catch (Exception ex) when (IsHandledFailure(ex) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
