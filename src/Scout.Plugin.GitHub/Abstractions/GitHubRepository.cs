namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// A repository the authenticated user can access. Minimal projection.
/// </summary>
/// <param name="FullName">Owner-qualified name, e.g. "octocat/hello-world".</param>
/// <param name="Private">True when the repository is private.</param>
public sealed record GitHubRepository(string FullName, bool Private);
