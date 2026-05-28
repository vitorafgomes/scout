namespace Scout.Llm.Abstractions;

/// <summary>
/// Platform-specific chat adapter (Discord, Slack, Teams).
/// One adapter instance is active per Scout process, selected via configuration.
/// Adapters translate platform events into <see cref="ChatMessage"/> and render
/// <see cref="ChatResponse"/> back into the platform's native format.
/// </summary>
public interface IChatAdapter
{
    /// <summary>Short identifier, e.g. "Discord". Must match the configuration key.</summary>
    string Name { get; }

    /// <summary>
    /// Start receiving platform events and routing them to <paramref name="handler"/>.
    /// Returns once the adapter is fully connected and listening.
    /// </summary>
    Task StartAsync(
        Func<ChatMessage, CancellationToken, Task<ChatResponse>> handler,
        CancellationToken cancellationToken);

    /// <summary>Stop receiving events and release resources.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
