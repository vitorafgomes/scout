using Scout.Conversation.Abstractions;

namespace Scout.Conversation.Memory;

/// <summary>
/// Default key policy: one history per channel + user pair. The two ids are
/// joined with the ASCII unit separator (U+001F) so neither id can collide into
/// the other's slot regardless of its content.
/// </summary>
public sealed class ConversationKeyProvider : IConversationKeyProvider
{
    private const char Separator = '';

    /// <inheritdoc />
    public string DeriveKey(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return $"{message.ChannelId}{Separator}{message.UserId}";
    }
}
