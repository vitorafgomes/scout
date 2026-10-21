namespace Scout.Plugin.GitHub.Contracts;

/// <summary>Detailed PR projection returned by <c>get_pull_request_details</c>.</summary>
public sealed record GitHubPullRequestDetails(
    int Number,
    string Title,
    string? Body,
    string State,
    string Author,
    DateTimeOffset UpdatedAt,
    string Url,
    int CommentCount,
    bool? Mergeable,
    string? MergeableState,
    int Additions,
    int Deletions,
    int ChangedFiles,
    string BaseRef,
    string HeadRef,
    IReadOnlyList<GitHubChangedFile> Files);
