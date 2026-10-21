using System.Text.RegularExpressions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Slack;

/// <summary>
/// Pure translator from Slack message payloads to the Scout
/// <see cref="ChatMessage"/>. No SlackNet types cross this boundary, so the
/// translator can be unit-tested without the SDK. Outbound rendering is owned
/// by <c>SlackResponseSink</c>, so this type only decides whether an inbound
/// message is for Scout and normalizes it.
/// </summary>
public sealed partial class SlackMessageTranslator
{
    // Matches Slack user mentions: <@U123> or <@U123|display-name>.
    [GeneratedRegex(@"<@(?<id>[A-Z0-9]+)(\|[^>]*)?>", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();

    public bool TryTranslate(
        InboundSlackMessage inbound,
        string botUserId,
        out ChatMessage? chatMessage)
        => TryTranslate(inbound, botUserId, out chatMessage, out _);

    internal bool TryTranslate(
        InboundSlackMessage inbound,
        string botUserId,
        out ChatMessage? chatMessage,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        chatMessage = null;
        reason = string.Empty;

        // Reject anything originating from a bot — our own messages, other bots,
        // and Slack's bot_message subtype. Prevents self-trigger loops on the
        // chat.update edits the sink performs in this same channel.
        if (!string.IsNullOrEmpty(inbound.BotId)
            || !string.IsNullOrEmpty(inbound.AppId)
            || string.Equals(inbound.Subtype, "bot_message", StringComparison.Ordinal))
        {
            reason = "bot-author";
            return false;
        }
        if (!string.IsNullOrEmpty(botUserId)
            && string.Equals(inbound.UserId, botUserId, StringComparison.Ordinal))
        {
            reason = "bot-author";
            return false;
        }

        // Message subtypes (channel joins, edits, etc.) carry no user prompt.
        // A normal user message has no subtype.
        if (!string.IsNullOrEmpty(inbound.Subtype))
        {
            reason = "subtype";
            return false;
        }

        // In a channel the bot must be explicitly @-mentioned so it does not
        // respond to unrelated traffic. DMs (channel_type "im") are implicitly
        // addressed to the bot and do not require a mention.
        var isDm = string.Equals(inbound.ChannelType, "im", StringComparison.Ordinal);
        var content = inbound.Text ?? string.Empty;
        var mentionsSelf = !string.IsNullOrEmpty(botUserId) && MentionsUser(content, botUserId);
        if (!isDm && !mentionsSelf)
        {
            reason = "no-mention";
            return false;
        }

        var stripped = StripBotMentions(content, botUserId);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            reason = "empty-after-strip";
            return false;
        }

        chatMessage = new ChatMessage(
            UserId: inbound.UserId,
            ChannelId: inbound.ChannelId,
            Text: stripped,
            Timestamp: inbound.Timestamp);
        return true;
    }

    private static bool MentionsUser(string content, string botUserId)
        => MentionPattern().Matches(content)
            .Any(m => m.Groups["id"].Value == botUserId);

    private static string StripBotMentions(string content, string botUserId)
    {
        var result = MentionPattern().Replace(content, match =>
            match.Groups["id"].Value == botUserId ? string.Empty : match.Value);
        return result.Trim();
    }
}
