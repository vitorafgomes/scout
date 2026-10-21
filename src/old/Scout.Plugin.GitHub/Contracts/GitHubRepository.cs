namespace Scout.Plugin.GitHub.Contracts;

/// <summary>Slim repository projection returned by <c>list_repositories</c>.</summary>
public sealed record GitHubRepository(
    string FullName,
    string Owner,
    string Name,
    bool Private,
    string? Description,
    string DefaultBranch,
    int OpenIssuesCount,
    int Stars,
    DateTimeOffset UpdatedAt,
    string Url);
