namespace Scout.Conversation.Routing;

/// <summary>
/// The skills a conversation carries from one turn to the next, so a turn that selects none still runs the previous
/// turn's. A channel that resets its conversation forgets them by key, as it clears the conversation's memory.
/// </summary>
public interface ICarriedSkills
{
    /// <summary>Forget the skills the conversation carries; its next turn without a selected skill runs none.</summary>
    /// <param name="key">The conversation key, as <see cref="Memory.IConversationKeyProvider"/> derives it.</param>
    void Forget(string key);
}
