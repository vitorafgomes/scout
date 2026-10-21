namespace Scout.Plugin.GitHub.Contracts;

/// <summary>File entry inside <see cref="GitHubPullRequestDetails"/>.</summary>
public sealed record GitHubChangedFile(
    string Filename,
    string Status,
    int Additions,
    int Deletions);
