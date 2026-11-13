using Microsoft.Extensions.Logging;

namespace Scout.Conversation.Streaming;

/// <summary>
/// Tuning for <see cref="StreamPump"/>. Transport-agnostic: the pump drives an
/// <c>IResponseSink</c>, so there is no edit delegate here. The sink's
/// <c>RenderCapabilities</c> can tighten cadence and length beyond these
/// defaults.
/// </summary>
public sealed record StreamPumpOptions
{
    /// <summary>Minimum interval between interim cumulative updates.</summary>
    public TimeSpan EditInterval { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Optional cap on rendered body length. When null, the sink's
    /// <c>MaxBodyLength</c> governs. When both apply, the smaller wins.
    /// </summary>
    public int? MaxBodyLength { get; init; }

    /// <summary>Cursor glyph appended to interim (non-terminal) updates.</summary>
    public string Cursor { get; init; } = "▌";

    /// <summary>Final text used when the stream produced nothing.</summary>
    public string EmptyStreamPlaceholder { get; init; } = "(no response from model)";

    /// <summary>
    /// Ceiling on a single pump run. The begin, each update and the completing write of a stream that ended well get
    /// a token that cancels when it passes. The terminal write of a run that ended on an error (a rate-limit, quota,
    /// sign-in, key, vision or failure message) is bounded by it whatever the sink does: a write the sink has not
    /// finished when it passes is abandoned and logged. A terminal write that starts after it has fired, the report
    /// of the timeout itself or an error caught late, runs past it for at most <see cref="TerminalWriteGrace"/>.
    /// Null disables it.
    /// </summary>
    public TimeSpan? OverallTimeout { get; init; }

    /// <summary>
    /// How long a terminal write gets when it starts after the overall timeout has already fired, so a timed-out
    /// reply can still say so. The run ends at most this long after the timeout, whether or not the sink finished the
    /// write. Never negative: the pump refuses to be built with a negative or infinite value.
    /// </summary>
    public TimeSpan TerminalWriteGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Clock seam for deterministic testing.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Logger for retries, rate-limit backoff, and diagnostics.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// User-facing text appended when the model rate-limits (HTTP 429). Rendered
    /// verbatim via the terminal Complete call, distinct from internal failures.
    /// </summary>
    public string RateLimitMessage { get; init; } =
        "I've hit the model's rate limit right now — please wait a moment and try again.";

    /// <summary>
    /// User-facing text appended when the provider refuses the request because the
    /// account has no credit or reached its spending limit. Rendered verbatim via
    /// the terminal Complete call; it carries no retry advice.
    /// </summary>
    public string QuotaExceededMessage { get; init; } =
        "The AI provider rejected the request: the account has no credit left or reached its spending limit. Add credit with the provider or choose another one on the AI Sources screen in Scout.";

    /// <summary>
    /// User-facing text appended instead of <see cref="QuotaExceededMessage"/> when the provider refuses with
    /// <c>credit_balance_too_low</c>, the Claude API account's credit having run out. A plain statement, never an error.
    /// </summary>
    public string CreditExhaustedMessage { get; init; } =
        "The AI provider's account has run out of credit. Add credit with the provider, or choose another AI Source in Scout Desktop.";

    /// <summary>
    /// User-facing text appended instead of <see cref="QuotaExceededMessage"/> when the ChatGPT plan refuses with
    /// <c>subscription_sharing_usage_limit_exceeded</c>, its usage limit for apps like Scout.
    /// </summary>
    public string PlanUsageLimitMessage { get; init; } =
        "The ChatGPT plan reached its usage limit for apps like Scout; try again later, or choose another AI Source in Scout Desktop.";

    /// <summary>
    /// User-facing text appended when the provider needs the user to sign in
    /// again. Rendered verbatim via the terminal Complete call.
    /// </summary>
    public string SignInRequiredMessage { get; init; } =
        "ChatGPT sign-in is missing or expired. Sign in again on the AI Sources screen in Scout.";

    /// <summary>
    /// User-facing text appended when a key-based provider rejects its key (HTTP 401). Rendered verbatim via the
    /// terminal Complete call.
    /// </summary>
    public string KeyRejectedMessage { get; init; } =
        "The AI Source rejected its key; check it in Scout Desktop.";

    /// <summary>
    /// User-facing text sent when a message carries a picture and the connected
    /// provider cannot read images. Rendered verbatim via the terminal Complete call.
    /// </summary>
    public string VisionUnavailableMessage { get; init; } =
        "The connected AI source cannot look at pictures; ask it in words or connect one that can.";

    /// <summary>
    /// User-facing text sent when the AI Source does not have the model Scout asked for (HTTP 404), formatted with the
    /// model id as <c>{0}</c>. A plain statement, never an error, true for any provider. Rendered verbatim via the terminal
    /// Complete call.
    /// </summary>
    public string ModelNotFoundMessage { get; init; } =
        "The AI Source does not have the model {0}. A local model is not installed yet or is still downloading: check AI Sources in Scout Desktop.";
}
