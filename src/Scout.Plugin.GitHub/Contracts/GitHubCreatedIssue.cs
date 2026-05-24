namespace Scout.Plugin.GitHub.Contracts;

/// <summary>Receipt returned by the descriptive write <c>create_issue</c>.</summary>
public sealed record GitHubCreatedIssue(int Number, string Url);
