namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// Read-only access to GitHub data via the authenticated user's token. Listing
/// only: no create, merge, push, or comment operations exist by design.
/// </summary>
public interface IGitHubClient
{
    /// <summary>List repositories the authenticated user can access.</summary>
    Task<IReadOnlyList<GitHubRepository>> ListRepositoriesAsync(CancellationToken ct);

    /// <summary>List open pull requests for <paramref name="owner"/>/<paramref name="repo"/>.</summary>
    Task<IReadOnlyList<GitHubPullRequest>> ListOpenPullRequestsAsync(
        string owner, string repo, CancellationToken ct);

    /// <summary>List open issues for <paramref name="owner"/>/<paramref name="repo"/>.</summary>
    Task<IReadOnlyList<GitHubIssue>> ListOpenIssuesAsync(
        string owner, string repo, CancellationToken ct);

    /// <summary>
    /// Fetch an overview of <paramref name="owner"/>/<paramref name="repo"/>:
    /// metadata, language breakdown, README excerpt, and top-level structure.
    /// Returns null when the repository is not found or not accessible.
    /// </summary>
    Task<RepositoryInfo?> GetRepositoryAsync(
        string owner, string repo, CancellationToken ct);
}
