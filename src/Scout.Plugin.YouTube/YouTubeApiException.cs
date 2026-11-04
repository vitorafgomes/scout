using System.Net;

namespace Scout.Plugin.YouTube;

/// <summary>
/// Raised when the YouTube API returns a non-success response. The
/// <see cref="Exception.Message"/> is user-safe and suitable for surfacing in
/// chat; it never carries the API key, the token or the request URL.
/// <see cref="StatusCode"/> is the originating HTTP status when known.
/// </summary>
public sealed class YouTubeApiException : Exception
{
    /// <summary>Creates the exception with a user-safe message and optional status.</summary>
    public YouTubeApiException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status that produced the failure, when known.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// True when the same request can succeed later unchanged: YouTube unavailable, a spent quota, a rate limit, or a
    /// sign-in the owner can renew.
    /// </summary>
    public bool Retryable { get; init; }
}
