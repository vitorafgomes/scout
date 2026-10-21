namespace Scout.Plugin.GitHub.Contracts;

/// <summary>Detailed issue projection returned by <c>get_issue</c>.</summary>
public sealed record GitHubIssueDetails(
    int Number,
    string Title,
    string? Body,
    string State,
    string Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Url,
    int CommentCount,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Assignees);
