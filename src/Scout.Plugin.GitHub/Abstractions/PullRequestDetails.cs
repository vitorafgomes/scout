namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// One pull request read for a review: its metadata and every changed file with the unified diff GitHub
/// returned for it. Read-only projection of two GitHub REST calls.
/// </summary>
/// <param name="Number">Pull request number within the repository.</param>
/// <param name="Title">Pull request title.</param>
/// <param name="Body">Description, or null when empty.</param>
/// <param name="AuthorLogin">Login of the author, or "unknown" when absent.</param>
/// <param name="State">"open" or "closed".</param>
/// <param name="Draft">True for a draft pull request.</param>
/// <param name="BaseRef">Branch the pull request merges into.</param>
/// <param name="HeadRef">Branch the pull request comes from.</param>
/// <param name="HeadSha">Commit the diff was taken at.</param>
/// <param name="Additions">Lines added across every file.</param>
/// <param name="Deletions">Lines deleted across every file.</param>
/// <param name="HtmlUrl">Browser URL for the pull request.</param>
/// <param name="ChangedFileCount">How many files the pull request changes, as GitHub counts them.</param>
/// <param name="Files">Changed files in GitHub's order, every page of them up to the 3,000 GitHub lists, so fewer than <see cref="ChangedFileCount"/> only on a larger pull request.</param>
public sealed record PullRequestDetails(
    int Number,
    string Title,
    string? Body,
    string AuthorLogin,
    string State,
    bool Draft,
    string BaseRef,
    string HeadRef,
    string HeadSha,
    int Additions,
    int Deletions,
    string HtmlUrl,
    int ChangedFileCount,
    IReadOnlyList<ChangedFile> Files)
{
    /// <summary>True when GitHub changes more files than <see cref="Files"/> lists.</summary>
    public bool FilesTruncated => ChangedFileCount > Files.Count;
}

/// <summary>One file a pull request changes.</summary>
/// <param name="Path">Repository-relative path of the file after the change.</param>
/// <param name="Status">"added", "modified", "removed", "renamed" or another GitHub status.</param>
/// <param name="Additions">Lines added in this file.</param>
/// <param name="Deletions">Lines deleted in this file.</param>
/// <param name="Patch">The unified diff, or null when GitHub omitted it (binary or too large).</param>
public sealed record ChangedFile(string Path, string Status, int Additions, int Deletions, string? Patch);

/// <summary>The head commit of a pull request and who opened it.</summary>
/// <param name="Sha">The commit the pull request's branch points at.</param>
/// <param name="AuthorLogin">Login of the author, or "unknown" when absent.</param>
public sealed record PullRequestHead(string Sha, string AuthorLogin);

/// <summary>A review given on a pull request.</summary>
/// <param name="Id">GitHub's id of the review.</param>
/// <param name="AuthorLogin">Login of who gave it, or "unknown".</param>
/// <param name="State"><c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>COMMENTED</c>, <c>DISMISSED</c> or <c>PENDING</c>.</param>
/// <param name="CommitId">The commit it was given on.</param>
/// <param name="HtmlUrl">Browser URL of the review.</param>
public sealed record PullRequestReview(long Id, string AuthorLogin, string State, string CommitId, string HtmlUrl);

/// <summary>One check on a commit, a check run or a commit status folded into the same shape.</summary>
/// <param name="Name">The check run's name, or the status's context.</param>
/// <param name="Status"><c>queued</c>, <c>in_progress</c> or <c>completed</c>.</param>
/// <param name="Conclusion"><c>success</c>, <c>failure</c>, <c>neutral</c>, <c>cancelled</c>, <c>skipped</c>, <c>timed_out</c> or <c>action_required</c>; null while not completed.</param>
/// <param name="Url">Where the check's details are.</param>
public sealed record PullRequestCheck(string Name, string Status, string? Conclusion, string Url);

/// <summary>The check runs read on a commit, and how many GitHub counts, which is more when they span several pages.</summary>
/// <param name="Checks">The check runs read.</param>
/// <param name="Total">How many check runs the commit has.</param>
public sealed record CheckRuns(IReadOnlyList<PullRequestCheck> Checks, int Total);
