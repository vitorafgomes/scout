namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// One pull request read for a review: its metadata and every changed file with the unified diff GitHub
/// returned for it. Read-only projection of two GitHub REST calls.
/// </summary>
/// <param name="Number">Pull request number within the repository.</param>
/// <param name="Title">Pull request title.</param>
/// <param name="Body">Description, or null when empty.</param>
/// <param name="AuthorLogin">Login of the author, or "unknown" when absent.</param>
/// <param name="State">"open" or "closed".</param>
/// <param name="Draft">True for a draft pull request.</param>
/// <param name="BaseRef">Branch the pull request merges into.</param>
/// <param name="HeadRef">Branch the pull request comes from.</param>
/// <param name="HeadSha">Commit the diff was taken at.</param>
/// <param name="Additions">Lines added across every file.</param>
/// <param name="Deletions">Lines deleted across every file.</param>
/// <param name="HtmlUrl">Browser URL for the pull request.</param>
/// <param name="ChangedFileCount">How many files the pull request changes, as GitHub counts them.</param>
/// <param name="Files">Changed files in GitHub's order; the first hundred, so fewer than <see cref="ChangedFileCount"/> on a large pull request.</param>
public sealed record PullRequestDetails(
    int Number,
    string Title,
    string? Body,
    string AuthorLogin,
    string State,
    bool Draft,
    string BaseRef,
    string HeadRef,
    string HeadSha,
    int Additions,
    int Deletions,
    string HtmlUrl,
    int ChangedFileCount,
    IReadOnlyList<ChangedFile> Files)
{
    /// <summary>True when GitHub changes more files than <see cref="Files"/> lists.</summary>
    public bool FilesTruncated => ChangedFileCount > Files.Count;
}

/// <summary>One file a pull request changes.</summary>
/// <param name="Path">Repository-relative path of the file after the change.</param>
/// <param name="Status">"added", "modified", "removed", "renamed" or another GitHub status.</param>
/// <param name="Additions">Lines added in this file.</param>
/// <param name="Deletions">Lines deleted in this file.</param>
/// <param name="Patch">The unified diff, or null when GitHub omitted it (binary or too large).</param>
public sealed record ChangedFile(string Path, string Status, int Additions, int Deletions, string? Patch);
