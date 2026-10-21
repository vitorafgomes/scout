namespace Scout.Plugin.CodeReview.Abstractions;

/// <summary>
/// A bounded read-only snapshot of a repository: its name, resolved default
/// branch, and the selected key files. This is the corpus handed to the LLM for
/// a repository-level review.
/// </summary>
/// <param name="FullName">Owner-qualified name, e.g. "octocat/hello-world".</param>
/// <param name="Branch">Resolved default branch the files were read from.</param>
/// <param name="Files">Selected text files, already capped and decoded.</param>
internal sealed record RepositorySnapshot(
    string FullName,
    string Branch,
    IReadOnlyList<RepositoryFile> Files);
