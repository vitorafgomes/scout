namespace Scout.Audit;

/// <summary>
/// Reads a pull request for its review: every changed file with its patch, which the job does not keep, once each time the
/// worker takes the review up, and its head commit alone before each batch. The host adapts the GitHub plugin's reads to
/// it, so this library never reaches GitHub.
/// </summary>
public interface IReviewPullRequests
{
    /// <summary>
    /// The head commit of pull request <paramref name="number"/> and every changed file with its patch; null when GitHub
    /// does not find it or refuses it. A read that fails throws.
    /// </summary>
    Task<ReviewPullRequest?> ReadAsync(string owner, string repo, int number, CancellationToken ct);

    /// <summary>
    /// The head commit of pull request <paramref name="number"/> alone, one read; null when GitHub does not find it or
    /// refuses it. A read that fails throws.
    /// </summary>
    Task<string?> HeadAsync(string owner, string repo, int number, CancellationToken ct);
}

/// <summary>A pull request as a review reads it again: its head commit and every changed file with its patch.</summary>
/// <param name="HeadSha">The commit the pull request's branch points at.</param>
/// <param name="Files">Every changed file, each with its patch, null when GitHub returned no diff for it.</param>
public sealed record ReviewPullRequest(string HeadSha, IReadOnlyList<ReviewFile> Files);
