using Scout.Llm.Abstractions;

namespace Scout.Conversation.Memory;

/// <summary>
/// Per-conversation history store. Loads a bounded, oldest-first snapshot ready
/// to seed <see cref="ChatRequest.History"/> and appends a user/assistant pair
/// atomically once a turn completes cleanly.
/// </summary>
public interface IConversationMemoryStore
{
    /// <summary>
    /// Returns a bounded snapshot of prior turns for the conversation, oldest-first,
    /// trimmed to the configured turn and character budgets (newest kept). Returns
    /// an empty list when nothing is stored or the conversation has gone idle.
    /// </summary>
    /// <param name="key">The conversation key. Must be non-null.</param>
    /// <returns>The replayable turns, oldest-first; never null.</returns>
    IReadOnlyList<ConversationTurn> Load(string key);

    /// <summary>
    /// Atomically appends a completed user/assistant turn-pair, evicting the
    /// oldest pairs FIFO past the configured turn budget and refreshing the
    /// conversation's last-touch time.
    /// </summary>
    /// <param name="key">The conversation key. Must be non-null.</param>
    /// <param name="user">The user turn.</param>
    /// <param name="assistant">The assistant turn.</param>
    void Append(string key, ConversationTurn user, ConversationTurn assistant);
}
