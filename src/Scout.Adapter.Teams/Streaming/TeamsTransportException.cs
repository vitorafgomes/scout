namespace Scout.Adapter.Teams.Streaming;

/// <summary>
/// Transport-level failure surfaced by <see cref="ITeamsActivitySender"/>. Carries
/// just the two signals the sink's resilience reacts to — the HTTP status and the
/// Teams error code — so unit tests can reproduce a 403 ContentStreamNotAllowed or
/// a 429 without referencing the Bot Connector / Microsoft.Rest exception types.
///
/// The production sender translates the SDK's <c>ErrorResponseException</c> into
/// this shape; see <c>TeamsServiceCollectionExtensions</c> notes on live wiring.
/// </summary>
public sealed class TeamsTransportException : Exception
{
    /// <summary>Teams error code returned when content streaming is rejected.</summary>
    public const string ContentStreamNotAllowed = "ContentStreamNotAllowed";

    public TeamsTransportException(
        int statusCode,
        string? errorCode = null,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"Teams transport failed with status {statusCode}.", innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>HTTP status the Teams service returned (e.g. 429, 403).</summary>
    public int StatusCode { get; }

    /// <summary>Teams error code, when present (e.g. "ContentStreamNotAllowed").</summary>
    public string? ErrorCode { get; }

    /// <summary>True for 429: the sink backs off and retries once.</summary>
    public bool IsRateLimited => StatusCode == 429;

    /// <summary>
    /// True for a 403 carrying <see cref="ContentStreamNotAllowed"/>: streaming is
    /// not permitted for this conversation, so the response is terminally canceled.
    /// </summary>
    public bool IsContentStreamNotAllowed =>
        StatusCode == 403
        && string.Equals(ErrorCode, ContentStreamNotAllowed, StringComparison.Ordinal);
}
