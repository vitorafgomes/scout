using Scout.Audit;
using Scout.Plugin.GitHub.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// The audit library's <see cref="IReviewPullRequests"/> over the GitHub plugin's <see cref="IGitHubClient"/>: one read of
/// the pull request with its changed files, copied as its head commit and each file's path and patch, and one read of its
/// head commit alone; Scout's earlier findings from its review comments, Scout's own being those by the login the client
/// signs in as or the bot's configured login.
/// </summary>
internal sealed class ReviewPullRequestsAdapter(IGitHubClient client, Microsoft.Extensions.Options.IOptions<Scout.Plugin.GitHub.GitHubOptions>? options = null) : IReviewPullRequests
{
    public async Task<Scout.Review.EarlierConversations?> EarlierAsync(string owner, string repo, int number, CancellationToken ct)
    {
        if (await client.ListReviewCommentsAsync(owner, repo, number, ct).ConfigureAwait(false) is not { } read)
        {
            return null;
        }

        string[] logins = [await client.GetSignedInLoginAsync(ct).ConfigureAwait(false), options?.Value.BotLogin ?? string.Empty];
        return Scout.Review.EarlierFindings.Own(
            [.. read.Comments.Select(c => new Scout.Review.ReviewCommentRead(
                c.Id, c.InReplyTo, c.AuthorLogin, c.Body, c.Path, c.Line, c.OriginalLine, c.OriginalCommitId ?? c.CommitId, c.CreatedAt, c.Url))],
            [.. logins.Where(l => l.Length > 0)],
            read.More);
    }

    public async Task<ReviewPullRequest?> ReadAsync(string owner, string repo, int number, CancellationToken ct) =>
        await client.GetPullRequestAsync(owner, repo, number, ct).ConfigureAwait(false) is { } read
            ? new ReviewPullRequest(read.HeadSha, [.. read.Files.Select(f => new ReviewFile(f.Path, f.Patch))])
            : null;

    public async Task<string?> HeadAsync(string owner, string repo, int number, CancellationToken ct) =>
        await client.GetPullRequestHeadAsync(owner, repo, number, ct).ConfigureAwait(false) is { Sha.Length: > 0 } head ? head.Sha : null;

    public async Task<Scout.Review.OfferChecks?> ChecksAsync(string owner, string repo, string sha, CancellationToken ct) =>
        Scout.Plugin.GitHub.PullRequestChecks.Offer(await Scout.Plugin.GitHub.PullRequestChecks.ReadAsync(client, owner, repo, sha, ct).ConfigureAwait(false));
}
