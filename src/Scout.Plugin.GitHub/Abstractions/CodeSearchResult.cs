namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>What GitHub's code search answered for one query in one repository's default branch.</summary>
/// <param name="TotalCount">How many files match, as GitHub counts them; more than <see cref="Hits"/> lists.</param>
/// <param name="Hits">The first page of matching files with the text fragments GitHub highlighted.</param>
public sealed record CodeSearchResult(int TotalCount, IReadOnlyList<CodeSearchHit> Hits);

/// <summary>One file GitHub's code search matched.</summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Fragments">The matching text fragments, possibly empty.</param>
public sealed record CodeSearchHit(string Path, IReadOnlyList<string> Fragments);
