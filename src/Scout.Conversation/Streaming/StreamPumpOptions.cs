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

    /// <summary>Hard ceiling on a single pump run. Null disables it.</summary>
    public TimeSpan? OverallTimeout { get; init; }

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
    /// User-facing text appended when the LLM provider is unreachable (connection
    /// refused, socket failure, or an internal timeout). Rendered verbatim via the
    /// terminal Complete call, distinct from internal failures and rate limits.
    /// </summary>
    public string ProviderUnavailableMessage { get; init; } =
        "I can't reach the language model right now. Make sure the model server is running, then try again.";
}
