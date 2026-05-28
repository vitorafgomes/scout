namespace Scout.Conversation.Abstractions;

/// <summary>
/// Describes what an <see cref="IResponseSink"/> can render, so the
/// stream-drive loop adapts cadence and interim behaviour to the platform
/// without knowing which platform it is. Shaped against the least-capable
/// target (Teams): cumulative updates, a hard edit-interval floor, and
/// final-only citations.
/// </summary>
public sealed record RenderCapabilities
{
    /// <summary>
    /// True when the sink can edit a posted message repeatedly with arbitrary
    /// cadence (Discord, Slack). False forces final-only rendering: the loop
    /// skips interim updates and emits a single Complete.
    /// </summary>
    public bool FineGrainedEdit { get; init; }

    /// <summary>Maximum renderable body length, in characters.</summary>
    public int MaxBodyLength { get; init; }

    /// <summary>
    /// True when the sink can render citations on interim updates. False for
    /// every platform today — citations land only on the terminal message.
    /// </summary>
    public bool SupportsInterimCitations { get; init; }

    /// <summary>
    /// Minimum interval the platform tolerates between edits, when stricter
    /// than the pump's configured cadence (Teams ~1s). Null means the pump's
    /// own interval governs.
    /// </summary>
    public TimeSpan? EditIntervalFloor { get; init; }
}
