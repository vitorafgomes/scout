using Scout.Conversation.Abstractions;

namespace Scout.Conversation.Memory;

/// <summary>
/// Default key policy: one history per channel + user pair. The channel id is
/// prefixed with its length and the two ids are joined with the ASCII unit
/// separator (U+001F), so neither id can collide into the other's slot
/// regardless of its content.
/// </summary>
public sealed class ConversationKeyProvider : IConversationKeyProvider
{
    private const char Separator = '\u001F';

    /// <inheritdoc />
    public string DeriveKey(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return $"{message.ChannelId.Length}:{message.ChannelId}{Separator}{message.UserId}";
    }
}
