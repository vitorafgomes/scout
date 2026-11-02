using Scout.Conversation.Abstractions;

namespace Scout.Conversation.Streaming;

/// <summary>
/// Wraps the sink of a voice message the conductor has already opened: every
/// rendered body starts with the quote of what was heard, and the pump's
/// <see cref="BeginAsync"/> is a no-op because the reply is already open.
/// </summary>
internal sealed class VoiceReplySink(IResponseSink inner, string prefix) : IResponseSink
{
    public RenderCapabilities Capabilities { get; } =
        inner.Capabilities.MaxBodyLength is > 0 and < int.MaxValue
            ? inner.Capabilities with
            {
                MaxBodyLength = Math.Max(1, inner.Capabilities.MaxBodyLength - prefix.Length),
            }
            : inner.Capabilities;

    public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken)
        => inner.UpdateAsync(prefix + cumulativeText, cancellationToken);

    public Task CompleteAsync(string finalText, IReadOnlyList<Citation> citations, CancellationToken cancellationToken)
        => inner.CompleteAsync(prefix + finalText, citations, cancellationToken);

    public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken)
        => inner.FailAsync(prefix + finalTextSoFar, reason, cancellationToken);
}
