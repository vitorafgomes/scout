namespace Scout.Core.Chat;

/// <summary>
/// Maps an inbound <see cref="ChatMessage"/> to a <see cref="ChatResponse"/>
/// produced by a specific plugin. The host asks every registered router in
/// turn; the first one that returns a non-null response wins. If no router
/// matches, the host falls back to its default handshake.
///
/// Implementations should be fast and deterministic — routing runs on the
/// hot path of every inbound message. Heavy lifting (cloud calls, RAG
/// retrieval, LLM composition) lives behind the returned response.
/// </summary>
public interface IMessageRouter
{
    Task<ChatResponse?> RouteAsync(ChatMessage message, CancellationToken cancellationToken);
}
