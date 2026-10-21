using Scout.Conversation.Abstractions;

namespace Scout.Conversation.Memory;

/// <summary>
/// Derives the storage key that isolates one conversation's history. The key
/// policy (currently channel + user) is owned here so it can change without
/// touching the store or the router.
/// </summary>
public interface IConversationKeyProvider
{
    /// <summary>
    /// Derives the conversation key for the given inbound message.
    /// </summary>
    /// <param name="message">The inbound message. Must be non-null.</param>
    /// <returns>A stable key unique to the message's conversation scope.</returns>
    string DeriveKey(ChatMessage message);
}
