namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// Read access to GitHub data via the authenticated user's token: listing, reading one
/// pull request, its comments, one issue and one file, the whole file tree, and code search. No create, merge, push or comment
/// operation exists here; a write, when one is added, is a separate declared function.
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

    /// <summary>
    /// Read pull request <paramref name="number"/> with its changed files and their diffs.
    /// Returns null when the pull request is not found or not accessible.
    /// </summary>
    Task<PullRequestDetails?> GetPullRequestAsync(
        string owner, string repo, int number, CancellationToken ct);

    /// <summary>
    /// List the first hundred changed files of pull request <paramref name="number"/> with their diffs.
    /// Returns null when the pull request is not found or not accessible.
    /// </summary>
    Task<IReadOnlyList<ChangedFile>?> ListPullRequestFilesAsync(
        string owner, string repo, int number, CancellationToken ct);

    /// <summary>
    /// List the review comments and the conversation comments of pull request <paramref name="number"/>, oldest first.
    /// Returns null when the pull request is not found or not accessible.
    /// </summary>
    Task<IReadOnlyList<PullRequestComment>?> ListPullRequestCommentsAsync(
        string owner, string repo, int number, CancellationToken ct);

    /// <summary>
    /// Read issue <paramref name="number"/>. Returns null when the issue is not found or not accessible.
    /// </summary>
    Task<IssueDetails?> GetIssueAsync(
        string owner, string repo, int number, CancellationToken ct);

    /// <summary>
    /// Read the file or directory at <paramref name="path"/>, at <paramref name="reference"/> (a branch,
    /// tag or commit) or the default branch when empty. Returns null when the path is not found.
    /// </summary>
    Task<RepositoryFile?> GetFileContentAsync(
        string owner, string repo, string path, string? reference, CancellationToken ct);

    /// <summary>
    /// List every file of the repository at <paramref name="reference"/>, or at the default branch when empty,
    /// in one recursive read. Returns null when the repository or the reference is not found.
    /// </summary>
    Task<RepositoryTree?> ListRepositoryFilesAsync(
        string owner, string repo, string? reference, CancellationToken ct);

    /// <summary>
    /// Search the repository's code on its default branch for <paramref name="query"/>, narrowed to
    /// <paramref name="path"/> and <paramref name="extension"/> when given; the first page of files with the
    /// fragments GitHub highlighted.
    /// </summary>
    Task<CodeSearchResult> SearchCodeAsync(
        string owner, string repo, string query, string? path, string? extension, CancellationToken ct);

    /// <summary>
    /// The commit a branch, a tag, a commit or the default branch (<paramref name="reference"/> empty) points at;
    /// null when the repository or the reference does not exist or is not accessible.
    /// </summary>
    Task<RepositoryCommit?> ResolveCommitAsync(string owner, string repo, string? reference, CancellationToken ct);

    /// <summary>
    /// Stream the repository's tarball at <paramref name="sha"/> into <paramref name="consume"/>, which reads it
    /// to the end; false when the repository or the commit does not exist or is not accessible.
    /// </summary>
    Task<bool> DownloadTarballAsync(string owner, string repo, string sha, Func<Stream, CancellationToken, Task> consume, CancellationToken ct);
}
