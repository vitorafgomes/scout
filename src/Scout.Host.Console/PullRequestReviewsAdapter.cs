using Scout.Audit;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// The GitHub plugin's <see cref="Scout.Plugin.GitHub.Abstractions.IPullRequestReviews"/> over the audit library's, so the
/// plugin's start and status of a pull request review reach the review job without either library referencing the other.
/// A start that ran answers its sentence and then each warning of its checks, one per line; a block's sentence is the answer.
/// </summary>
internal sealed class PullRequestReviewsAdapter(Scout.Audit.IPullRequestReviews reviews) : Scout.Plugin.GitHub.Abstractions.IPullRequestReviews
{
    public async Task<string> StartAsync(
        string owner, string repo, int number, string headSha, IReadOnlyList<ChangedFile> files, int diffsLeftOut, int notListed, PullRequestCheckCounts? checks, CancellationToken ct)
    {
        var start = await reviews.StartAsync(
            owner, repo, number, headSha, [.. files.Select(f => new ReviewFile(f.Path, f.Patch))], diffsLeftOut, notListed,
            checks is null ? null : new ReviewCheckCounts(checks.Passing, checks.Pending, checks.Failing), ct).ConfigureAwait(false);
        return start.Started
            ? string.Join("\n", [start.Sentence, .. start.Preflight.Checks.Where(c => c.Outcome == PreflightOutcome.Warning).Select(c => c.Sentence)])
            : start.Sentence;
    }

    public Task<string> StatusAsync(string owner, string repo, int number, CancellationToken ct) => reviews.StatusAsync(owner, repo, number, ct);
}
