namespace Scout.Plugin.GitHub.Contracts;

/// <summary>
/// Uniform envelope returned by every GitHub kernel function. Errors are
/// surfaced as data (never thrown to the LLM) so the model can read
/// <c>Error</c> codes and decide whether to retry or surface to the user.
/// </summary>
/// <typeparam name="T">Payload type when <c>Success</c> is true.</typeparam>
/// <param name="Success">True when the call completed and <c>Value</c> is set.</param>
/// <param name="Value">Payload; null on failure.</param>
/// <param name="Error">
/// Stable, machine-readable error code (e.g. <c>credentials-invalid</c>,
/// <c>not-found</c>, <c>rate-limited</c>, <c>github-api-error</c>).
/// </param>
/// <param name="Detail">Human-readable detail intended for chat surface.</param>
/// <param name="RetryAfter">
/// Wall-clock time after which a retry is safe. Set for
/// <c>rate-limited</c>; null otherwise.
/// </param>
public sealed record GitHubResult<T>(
    bool Success,
    T? Value,
    string? Error,
    string? Detail,
    DateTimeOffset? RetryAfter)
{
    /// <summary>Construct a success envelope.</summary>
    public static GitHubResult<T> Ok(T value) => new(true, value, null, null, null);

    /// <summary>Construct an error envelope.</summary>
    public static GitHubResult<T> Fail(string error, string? detail = null, DateTimeOffset? retryAfter = null)
        => new(false, default, error, detail, retryAfter);
}
