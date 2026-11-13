namespace Scout.Conversation.Abstractions;

/// <summary>
/// Maps an inbound <see cref="ChatMessage"/> to a streaming
/// <see cref="ChatResponse"/>. A single responder for v1: always returns a
/// non-null streaming response, never a <see cref="Task"/> and never null.
/// Multi-router fan-out belongs to the future agent-router project.
/// </summary>
public interface IConversationRouter
{
    /// <summary>Route a message to its streaming response. Never null.</summary>
    ChatResponse Route(ChatMessage message);
}
