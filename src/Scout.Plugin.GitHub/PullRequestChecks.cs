using Scout.Plugin.GitHub.Abstractions;
using Scout.Review;

namespace Scout.Plugin.GitHub;

/// <summary>The checks of a commit as Scout reads them, for the verdict and for a review's offer.</summary>
public static class PullRequestChecks
{
    /// <summary>The check runs and statuses of <paramref name="sha"/>, read together; null when GitHub has more than Scout read. A failed read throws.</summary>
    public static async Task<IReadOnlyList<PullRequestCheck>?> ReadAsync(IGitHubClient reads, string owner, string repo, string sha, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reads);
        var runsTask = reads.ListCheckRunsAsync(owner, repo, sha, ct);
        var statusesTask = reads.ListStatusesAsync(owner, repo, sha, ct);
        await Task.WhenAll(runsTask, statusesTask).ConfigureAwait(false);
        var runs = await runsTask.ConfigureAwait(false);
        var statuses = await statusesTask.ConfigureAwait(false);
        return runs.Total > runs.Checks.Count || statuses.Count >= GitHubClient.FilesPage ? null : [.. runs.Checks, .. statuses];
    }

    /// <summary>
    /// <paramref name="checks"/> counted for a review's offer: passed (success, neutral, skipped), still running, and failed;
    /// null when they were not all read.
    /// </summary>
    public static OfferChecks? Offer(IReadOnlyList<PullRequestCheck>? checks)
    {
        if (checks is null)
        {
            return null;
        }

        var running = checks.Count(c => c.Status != "completed");
        var passed = checks.Count(c => c.Status == "completed" && c.Conclusion is ("success" or "neutral" or "skipped"));
        return new OfferChecks(passed, checks.Count - running - passed, running, true);
    }
}
