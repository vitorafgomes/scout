namespace Scout.Conversation.Abstractions;

/// <summary>
/// Platform-specific chat adapter (Discord, Slack, Teams). One adapter is active
/// per Scout process, selected via configuration. The adapter normalizes
/// inbound platform events into <see cref="ChatMessage"/>, creates an
/// <see cref="IResponseSink"/> bound to the ORIGINATING channel only (the
/// descriptive-write boundary — a reply may write only where the message came
/// from), and invokes the handler with both.
/// </summary>
public interface IChatAdapter
{
    /// <summary>Short identifier, e.g. "Discord". Must match the configuration key.</summary>
    string Name { get; }

    /// <summary>
    /// Start receiving platform events. For each inbound message addressed to
    /// the bot, build a channel-bound <see cref="IResponseSink"/> and invoke
    /// <paramref name="handler"/>. Returns once connected and listening.
    /// </summary>
    Task StartAsync(
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken);

    /// <summary>Stop receiving events and release resources.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
