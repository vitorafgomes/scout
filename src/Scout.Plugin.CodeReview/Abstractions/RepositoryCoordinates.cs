namespace Scout.Plugin.CodeReview.Abstractions;

/// <summary>
/// Owner and name of a GitHub repository, parsed from a clone or browser URL.
/// </summary>
/// <param name="Owner">Repository owner or organization login, e.g. "octocat".</param>
/// <param name="Repo">Repository name, e.g. "hello-world".</param>
public sealed record RepositoryCoordinates(string Owner, string Repo);
