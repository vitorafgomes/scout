namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// An open pull request. Minimal projection.
/// </summary>
/// <param name="Number">Pull request number within the repository.</param>
/// <param name="Title">Pull request title.</param>
/// <param name="AuthorLogin">Login of the author, or "unknown" when absent.</param>
public sealed record GitHubPullRequest(int Number, string Title, string AuthorLogin);
