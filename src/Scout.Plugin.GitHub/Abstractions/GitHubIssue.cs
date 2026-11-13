namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// An open issue. Minimal projection.
/// </summary>
/// <param name="Number">Issue number within the repository.</param>
/// <param name="Title">Issue title.</param>
/// <param name="AuthorLogin">Login of the author, or "unknown" when absent.</param>
public sealed record GitHubIssue(int Number, string Title, string AuthorLogin);
