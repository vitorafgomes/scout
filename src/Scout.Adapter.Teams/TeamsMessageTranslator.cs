using System.Text.RegularExpressions;
using Microsoft.Bot.Schema;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams;

/// <summary>
/// Pure translator from a Teams inbound payload to the Scout
/// <see cref="ChatMessage"/>. No Bot Framework types cross the public boundary
/// (the <c>Activity</c> projection is an <c>internal static</c> helper), so the
/// decision logic is unit-testable without constructing SDK primitives.
///
/// Rules: skip non-message activities and the bot's own echoes; skip empties;
/// strip the bot's @-mention, by the tag of the mention entity that carries
/// its id and by its display name; treat 1:1 chats as implicitly addressed to
/// the bot. In a group chat (or channel) the message is accepted
/// only when it @-mentions the bot, mirroring Discord's guild rule: a mention
/// entity carrying the bot's id, or the bot's display name in an <c>at</c> tag;
/// without either, another user's mention never counts as the bot's.
/// </summary>
public sealed partial class TeamsMessageTranslator
{
    // Teams entity-mentions surface in text as <at>Display Name</at>; strip them.
    [GeneratedRegex(@"<at>.*?</at>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AtMentionTag();

    public bool TryTranslate(InboundTeamsMessage inbound, out ChatMessage? chatMessage)
        => TryTranslate(inbound, out chatMessage, out _);

#pragma warning disable CA1822 // Instance member: the translator is DI-injected and called through an instance by adapters and tests
    internal bool TryTranslate(
        InboundTeamsMessage inbound,
        out ChatMessage? chatMessage,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        chatMessage = null;
        reason = string.Empty;

        if (!string.Equals(inbound.ActivityType, ActivityTypes.Message, StringComparison.OrdinalIgnoreCase))
        {
            reason = "not-a-message";
            return false;
        }

        // The bot must never answer its own messages.
        if (!string.IsNullOrEmpty(inbound.RecipientId)
            && string.Equals(inbound.FromId, inbound.RecipientId, StringComparison.Ordinal))
        {
            reason = "bot-author";
            return false;
        }

        // Group/channel traffic requires an explicit bot mention; 1:1 chats are
        // implicitly addressed to the bot.
        var mentionsBot = MentionsBot(inbound);
        if (inbound.IsGroup && !mentionsBot)
        {
            reason = "no-mention";
            return false;
        }

        var stripped = StripBotMention(inbound);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            reason = "empty-after-strip";
            return false;
        }

        chatMessage = new ChatMessage(
            UserId: inbound.FromId,
            ChannelId: inbound.ConversationId,
            Text: stripped,
            Timestamp: inbound.Timestamp);
        return true;
    }
#pragma warning restore CA1822

    private static bool MentionsBot(InboundTeamsMessage inbound)
    {
        if (!string.IsNullOrEmpty(inbound.RecipientId)
            && inbound.MentionedIds?.Contains(inbound.RecipientId, StringComparer.Ordinal) == true)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(inbound.RecipientMention))
        {
            return false;
        }
        var tag = $"<at>{inbound.RecipientMention}</at>";
        return (inbound.Text ?? string.Empty).Contains(tag, StringComparison.Ordinal);
    }

    private static string StripBotMention(InboundTeamsMessage inbound)
    {
        var result = inbound.Text ?? string.Empty;
        foreach (var tag in inbound.RecipientMentionTexts ?? [])
        {
            if (tag.Length > 0)
            {
                result = result.Replace(tag, string.Empty, StringComparison.Ordinal);
            }
        }

        result = string.IsNullOrWhiteSpace(inbound.RecipientMention)
            ? AtMentionTag().Replace(result, string.Empty)
            : result.Replace($"<at>{inbound.RecipientMention}</at>", string.Empty, StringComparison.Ordinal);
        return result.Trim();
    }

    // Pure projection of the inbound Activity fields into the translator DTO.
    // internal static so the inbound glue is unit-testable and the live adapter
    // shares the exact mapping.
    internal static InboundTeamsMessage ProjectInbound(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        // ConversationType "personal" is a 1:1 chat; "groupChat"/"channel" are
        // groups. IsGroup flag on the conversation account is the canonical
        // signal when present.
        var conversation = activity.Conversation;
        var isGroup = conversation?.IsGroup == true
            || string.Equals(conversation?.ConversationType, "groupChat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(conversation?.ConversationType, "channel", StringComparison.OrdinalIgnoreCase);

        var recipientId = activity.Recipient?.Id;
        var mentions = activity.GetMentions();

        return new InboundTeamsMessage(
            ActivityId: activity.Id ?? string.Empty,
            ActivityType: activity.Type ?? string.Empty,
            FromId: activity.From?.Id ?? string.Empty,
            ConversationId: conversation?.Id ?? string.Empty,
            Text: activity.Text ?? string.Empty,
            IsGroup: isGroup,
            RecipientId: recipientId,
            RecipientMention: activity.Recipient?.Name,
            Timestamp: activity.Timestamp,
            MentionedIds: [.. mentions.Select(m => m.Mentioned?.Id).OfType<string>()],
            RecipientMentionTexts: [.. mentions.Where(m => m.Mentioned?.Id is { } id && id == recipientId).Select(m => m.Text).OfType<string>()]);
    }
}
