namespace Scout.Conversation.Abstractions;

/// <summary>
/// Platform-specific outbound sink, bound to a single originating channel. The
/// conversation core owns the stream-drive loop and calls this thin sink;
/// adapters implement only the transport edits. Designed against the
/// least-capable platform (Teams): <see cref="UpdateAsync"/> receives the FULL
/// cumulative text on every call, never a delta, so a sink that can only
/// replace the whole message body still works.
///
/// Lifecycle: exactly one <see cref="BeginAsync"/>, then zero or more
/// <see cref="UpdateAsync"/> (monotonic cumulative text), then exactly one
/// <see cref="CompleteAsync"/> OR <see cref="FailAsync"/>. Any update or second
/// terminal call after a terminal one throws
/// <see cref="InvalidOperationException"/>.
/// </summary>
public interface IResponseSink
{
    /// <summary>What this sink can render. Stable for the sink's lifetime.</summary>
    RenderCapabilities Capabilities { get; }

    /// <summary>Open the response (e.g. post the initial placeholder message).</summary>
    Task BeginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Render the FULL cumulative text so far. Not a delta. Cumulative text is
    /// monotonic across calls.
    /// </summary>
    Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken);

    /// <summary>
    /// Finish successfully: render the final text plus any citations. Terminal.
    /// </summary>
    Task CompleteAsync(
        string finalText,
        IReadOnlyList<Citation> citations,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finish with failure: render whatever text was accumulated plus an error
    /// indication derived from <paramref name="reason"/>. Terminal.
    /// </summary>
    Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken);
}
