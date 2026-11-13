namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// The writes GitHub takes from Scout, each from a declared function the write gate lets through: a review with
/// comments on a pull request, a verdict on one, a new issue, a comment on a pull request's or an issue's conversation,
/// and a reply to a review comment. Nothing here merges, pushes, edits or deletes.
/// </summary>
public interface IGitHubWriteClient
{
    /// <summary>Post a review that comments, never approves or requests changes, on pull request <paramref name="number"/>.</summary>
    Task<PostedReview> SubmitReviewAsync(
        string owner, string repo, int number, string body, IReadOnlyList<ReviewComment> comments, CancellationToken ct);

    /// <summary>
    /// Approve pull request <paramref name="number"/> or request changes on it, with a body and comments on the diff, on
    /// <paramref name="commitId"/>, the commit the review read.
    /// </summary>
    Task<PostedReview> SubmitVerdictAsync(
        string owner, string repo, int number, ReviewVerdict verdict, string body, IReadOnlyList<ReviewComment> comments, string commitId, CancellationToken ct);

    /// <summary>Open an issue on <paramref name="owner"/>/<paramref name="repo"/>.</summary>
    Task<CreatedIssue> CreateIssueAsync(
        string owner, string repo, string title, string body, IReadOnlyList<string> labels, CancellationToken ct);

    /// <summary>Comment on the conversation of issue or pull request <paramref name="number"/>, with exactly <paramref name="body"/>.</summary>
    Task<PostedComment> CommentOnIssueAsync(string owner, string repo, int number, string body, CancellationToken ct);

    /// <summary>
    /// Comment on the conversation of issue or pull request <paramref name="number"/>, with exactly <paramref name="body"/>,
    /// signed as <paramref name="signedAs"/>'s writes are, such as the tests line after a verdict.
    /// </summary>
    Task<PostedComment> CommentOnIssueAsync(string owner, string repo, int number, string body, GitHubWriteKind signedAs, CancellationToken ct);

    /// <summary>Who signs a write of <paramref name="kind"/> on <paramref name="owner"/>/<paramref name="repo"/>.</summary>
    GitHubSigner SignerFor(GitHubWriteKind kind, string owner, string repo);

    /// <summary>Reply to review comment <paramref name="commentId"/> of pull request <paramref name="number"/>, with exactly <paramref name="body"/>.</summary>
    Task<PostedComment> ReplyToReviewCommentAsync(
        string owner, string repo, int number, long commentId, string body, CancellationToken ct);
}

/// <summary>The verdict a review gives a pull request, beyond a comment.</summary>
public enum ReviewVerdict
{
    /// <summary>The review approves the pull request.</summary>
    Approve,

    /// <summary>The review requests changes before the pull request merges.</summary>
    RequestChanges,
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

/// <summary>A comment GitHub accepted.</summary>
/// <param name="Id">GitHub's id of the comment.</param>
/// <param name="HtmlUrl">Browser URL of the comment.</param>
public sealed record PostedComment(long Id, string HtmlUrl);
