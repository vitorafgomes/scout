namespace Scout.Plugin.GitHub.Contracts;

/// <summary>Slim PR projection returned by <c>list_pull_requests</c>.</summary>
public sealed record GitHubPullRequestSummary(
    int Number,
    string Title,
    string State,
    string Author,
    DateTimeOffset UpdatedAt,
    string Url,
    int CommentCount,
    string BaseRef,
    string HeadRef,
    bool Draft);
