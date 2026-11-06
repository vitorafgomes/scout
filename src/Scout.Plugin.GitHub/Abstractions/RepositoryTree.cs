namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>The files of a repository at one reference, as GitHub's recursive tree lists them.</summary>
/// <param name="Reference">The branch, tag or commit the tree was read at.</param>
/// <param name="Truncated">True when GitHub cut the listing (a tree over its limit); the entries are then a prefix of the repository.</param>
/// <param name="Entries">Every file, directories left out, in GitHub's order.</param>
public sealed record RepositoryTree(string Reference, bool Truncated, IReadOnlyList<RepositoryEntry> Entries);

/// <summary>One file of a repository tree.</summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Size">Size in bytes GitHub reported; 0 when it did not.</param>
public sealed record RepositoryEntry(string Path, long Size);
