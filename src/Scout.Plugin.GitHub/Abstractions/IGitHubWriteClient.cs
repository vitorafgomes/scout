namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// The two writes GitHub takes from Scout, each only from the executor that runs an action the owner approved:
/// a review with comments on a pull request, and a new issue. Nothing here merges, pushes, edits or deletes.
/// </summary>
public interface IGitHubWriteClient
{
    /// <summary>Post a review that comments, never approves or requests changes, on pull request <paramref name="number"/>.</summary>
    Task<PostedReview> SubmitReviewAsync(
        string owner, string repo, int number, string body, IReadOnlyList<ReviewComment> comments, CancellationToken ct);

    /// <summary>Open an issue on <paramref name="owner"/>/<paramref name="repo"/>.</summary>
    Task<CreatedIssue> CreateIssueAsync(
        string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken ct);
}

/// <summary>One review comment anchored to a line of the pull request's diff, on the side of the change.</summary>
/// <param name="Path">Repository-relative path of the changed file.</param>
/// <param name="Line">Line number in the file after the change; it must appear in the diff.</param>
/// <param name="Body">The comment text.</param>
public sealed record ReviewComment(string Path, int Line, string Body);

/// <summary>A review GitHub accepted.</summary>
/// <param name="Id">GitHub's id of the review.</param>
/// <param name="HtmlUrl">Browser URL of the review.</param>
public sealed record PostedReview(long Id, string HtmlUrl);

/// <summary>An issue GitHub created.</summary>
/// <param name="Number">The issue number within the repository.</param>
/// <param name="HtmlUrl">Browser URL of the issue.</param>
public sealed record CreatedIssue(int Number, string HtmlUrl);
