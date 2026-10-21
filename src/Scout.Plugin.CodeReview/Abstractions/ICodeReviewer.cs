namespace Scout.Plugin.CodeReview.Abstractions;

/// <summary>
/// Untrusted content handed to the reviewer for one review. Exactly one of the
/// content fields is populated per <see cref="ReviewKind"/>: <see cref="Snippet"/>
/// for snippet kinds, <see cref="Diff"/> for a pull-request review, and
/// <see cref="Files"/> for a repository review. Hints are optional and steer the
/// model without changing the review kind.
/// </summary>
/// <param name="Snippet">Source snippet to review; null for diff/repository kinds.</param>
/// <param name="Diff">Unified diff to review; null for snippet/repository kinds.</param>
/// <param name="Files">Repository files to review; null for snippet/diff kinds.</param>
/// <param name="Framework">Optional test framework hint (e.g. "xUnit").</param>
/// <param name="Level">Optional explanation level hint (e.g. "beginner").</param>
/// <param name="RepositoryName">Optional repository name for repository reviews.</param>
public sealed record CodeReviewInput(
    string? Snippet = null,
    string? Diff = null,
    IReadOnlyList<ReviewFile>? Files = null,
    string? Framework = null,
    string? Level = null,
    string? RepositoryName = null);

/// <summary>
/// One file passed into a repository-level review. This is the reviewing layer's
/// type; it is intentionally distinct from the fetch layer's
/// <c>RepositoryFile</c> and hand-mapped in <c>AnalyzeRepositoryAsync</c>, so the
/// reviewing contract and the fetch contract stay independent.
/// </summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Content">Decoded text content.</param>
public sealed record ReviewFile(string Path, string Content);

/// <summary>
/// Runs a single review against the configured language model. One method serves
/// every <see cref="ReviewKind"/>; the kind selects the system prompt and how the
/// untrusted input is framed.
/// </summary>
public interface ICodeReviewer
{
    /// <summary>
    /// Produce a review of <paramref name="input"/> for the given
    /// <paramref name="kind"/>.
    /// </summary>
    /// <param name="kind">The kind of review to run.</param>
    /// <param name="input">The untrusted content and optional hints.</param>
    /// <param name="ct">Cancels the underlying model call.</param>
    /// <returns>The model's non-empty review text.</returns>
    /// <exception cref="LlmReviewException">Thrown when the model returns no usable content.</exception>
    Task<string> ReviewAsync(ReviewKind kind, CodeReviewInput input, CancellationToken ct);
}
