namespace Scout.Plugin.CodeQA.Indexing;

/// <summary>
/// Narrow seam over the subset of GitHub operations the ingestor needs.
/// Exists so <see cref="GitHubIngestor"/> can be unit-tested without
/// standing up a real Octokit client or hitting the network.
/// </summary>
internal interface IGitHubTreeClient
{
    /// <summary>
    /// Resolves a branch ref to the commit SHA at its HEAD.
    /// </summary>
    Task<GitReference> GetBranchHeadAsync(
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the recursive tree for a commit. The <see cref="GitTreeResponse.Truncated"/>
    /// flag propagates GitHub's own truncation signal; callers must treat a
    /// truncated tree as a hard error rather than silently ingest a subset.
    /// </summary>
    Task<GitTreeResponse> GetRecursiveTreeAsync(
        string owner,
        string repo,
        string commitSha,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a single blob's raw bytes by SHA. Callers are responsible for
    /// UTF-8 decoding and binary-heuristic rejection.
    /// </summary>
    Task<byte[]> GetBlobRawAsync(
        string owner,
        string repo,
        string blobSha,
        CancellationToken cancellationToken);
}

internal sealed record GitReference(string CommitSha);

internal sealed record GitTreeResponse(IReadOnlyList<GitTreeEntry> Items, bool Truncated);

internal sealed record GitTreeEntry(string Path, string Sha, int SizeBytes, GitTreeEntryType Type);

internal enum GitTreeEntryType
{
    Blob,
    Tree,
    Commit,
}
