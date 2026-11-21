using Scout.Audit;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// The audit library's <see cref="IReviewPullRequests"/> over the GitHub plugin's <see cref="IGitHubClient"/>: one read of
/// the pull request with its changed files, copied as its head commit and each file's path and patch, and one read of its
/// head commit alone.
/// </summary>
internal sealed class ReviewPullRequestsAdapter(IGitHubClient client) : IReviewPullRequests
{
    public async Task<ReviewPullRequest?> ReadAsync(string owner, string repo, int number, CancellationToken ct) =>
        await client.GetPullRequestAsync(owner, repo, number, ct).ConfigureAwait(false) is { } read
            ? new ReviewPullRequest(read.HeadSha, [.. read.Files.Select(f => new ReviewFile(f.Path, f.Patch))])
            : null;

    public async Task<string?> HeadAsync(string owner, string repo, int number, CancellationToken ct) =>
        await client.GetPullRequestHeadAsync(owner, repo, number, ct).ConfigureAwait(false) is { Sha.Length: > 0 } head ? head.Sha : null;

    public async Task<Scout.Review.OfferChecks?> ChecksAsync(string owner, string repo, string sha, CancellationToken ct) =>
        Scout.Plugin.GitHub.PullRequestChecks.Offer(await Scout.Plugin.GitHub.PullRequestChecks.ReadAsync(client, owner, repo, sha, ct).ConfigureAwait(false));
}
