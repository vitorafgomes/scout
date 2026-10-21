namespace Scout.Plugin.CodeReview.Abstractions;

/// <summary>
/// Read-only fetch of a bounded set of key files from a GitHub repository for
/// review. Implementations resolve the default branch, list the tree, select
/// representative files, and decode their text within configured size caps.
/// </summary>
internal interface IRepositoryFetcher
{
    /// <summary>
    /// Fetch a capped snapshot of <paramref name="coords"/>. Non-success GitHub
    /// responses surface as <see cref="CodeReviewFetchException"/>; timeouts and
    /// transport faults bubble unchanged.
    /// </summary>
    /// <param name="coords">Repository owner and name.</param>
    /// <param name="ct">Cancels the underlying GitHub requests.</param>
    /// <returns>A bounded, decoded snapshot of the repository's key files.</returns>
    Task<RepositorySnapshot> FetchAsync(RepositoryCoordinates coords, CancellationToken ct);
}
