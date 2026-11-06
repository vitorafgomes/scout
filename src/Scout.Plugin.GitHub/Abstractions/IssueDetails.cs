namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>One issue read in full. GitHub's issues endpoint also answers for a pull request number; <see cref="IsPullRequest"/> says so.</summary>
/// <param name="Number">Issue number within the repository.</param>
/// <param name="Title">Issue title.</param>
/// <param name="Body">Description, or null when empty.</param>
/// <param name="AuthorLogin">Login of the author, or "unknown" when absent.</param>
/// <param name="State">"open" or "closed".</param>
/// <param name="Labels">Label names; may be empty.</param>
/// <param name="CommentCount">Number of comments.</param>
/// <param name="HtmlUrl">Browser URL for the issue.</param>
/// <param name="IsPullRequest">True when the number belongs to a pull request, not an issue.</param>
public sealed record IssueDetails(
    int Number,
    string Title,
    string? Body,
    string AuthorLogin,
    string State,
    IReadOnlyList<string> Labels,
    int CommentCount,
    string HtmlUrl,
    bool IsPullRequest);
