namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// A comment on a pull request: a review comment anchored to a file line, or a conversation comment with no anchor.
/// </summary>
/// <param name="AuthorLogin">Login of the author, or "unknown" when absent.</param>
/// <param name="Body">The comment text.</param>
/// <param name="Path">The file the comment is anchored to, or null for a conversation comment.</param>
/// <param name="Line">The line in the file, or null when the comment is not anchored or the line is gone.</param>
/// <param name="CreatedAt">When the comment was posted.</param>
public sealed record PullRequestComment(
    string AuthorLogin,
    string Body,
    string? Path,
    int? Line,
    DateTimeOffset CreatedAt);
