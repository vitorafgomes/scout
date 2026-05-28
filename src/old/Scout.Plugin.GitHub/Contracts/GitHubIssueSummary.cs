namespace Scout.Plugin.GitHub.Contracts;

/// <summary>Slim issue projection returned by <c>list_issues</c>.</summary>
public sealed record GitHubIssueSummary(
    int Number,
    string Title,
    string State,
    string Author,
    DateTimeOffset UpdatedAt,
    string Url,
    int CommentCount,
    IReadOnlyList<string> Labels);
