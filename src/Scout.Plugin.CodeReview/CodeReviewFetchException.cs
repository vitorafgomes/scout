using System.Net;

namespace Scout.Plugin.CodeReview;

/// <summary>
/// Raised when fetching repository contents from GitHub fails. The
/// <see cref="Exception.Message"/> is user-safe and suitable for surfacing in
/// chat. <see cref="StatusCode"/> is the originating HTTP status when known.
/// </summary>
public sealed class CodeReviewFetchException : Exception
{
    /// <summary>Creates the exception with a user-safe message and optional status.</summary>
    public CodeReviewFetchException(
        string message, HttpStatusCode? statusCode = null, bool rateLimited = false)
        : base(message)
    {
        StatusCode = statusCode;
        RateLimited = rateLimited;
    }

    /// <summary>HTTP status that produced the failure, when known.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// True when the failure is a GitHub rate-limit (a 403 with
    /// <c>X-RateLimit-Remaining: 0</c>). Rate-limit affects the whole repository
    /// and must abort the fetch; a plain 403 on a single blob is best-effort
    /// skippable.
    /// </summary>
    public bool RateLimited { get; }
}
