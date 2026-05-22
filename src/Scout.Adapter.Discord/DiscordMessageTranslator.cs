using System.Text;
using System.Text.RegularExpressions;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord;

/// <summary>
/// Pure translator between Discord message payloads and the Scout core DTOs.
/// No Discord.Net types cross this boundary, so the translator can be unit-
/// tested without mocking SDK primitives.
///
/// - <c>TryTranslate</c> decides whether an inbound Discord message is
///   intended for Scout (the bot was mentioned and the message carries real
///   content) and returns the corresponding <see cref="ChatMessage"/>.
/// - <see cref="Render"/> produces the outbound string that the adapter posts
///   back to Discord.
/// </summary>
public sealed partial class DiscordMessageTranslator
{
    // Matches <@ID> and <@!ID> — Discord's two mention syntaxes.
    [GeneratedRegex(@"<@!?(?<id>\d+)>", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();

    public bool TryTranslate(
        InboundDiscordMessage inbound,
        ulong botUserId,
        out ChatMessage? chatMessage)
        => TryTranslate(inbound, botUserId, out chatMessage, out _);

    internal bool TryTranslate(
        InboundDiscordMessage inbound,
        ulong botUserId,
        out ChatMessage? chatMessage,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        chatMessage = null;
        reason = string.Empty;

        if (inbound.AuthorId == botUserId)
        {
            reason = "bot-author";
            return false;
        }
        if (!inbound.MentionsSelf)
        {
            reason = "no-mention";
            return false;
        }

        var stripped = StripBotMentions(inbound.Content ?? string.Empty, botUserId);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            reason = "empty-after-strip";
            return false;
        }

        chatMessage = new ChatMessage(
            UserId: inbound.AuthorId.ToString(),
            ChannelId: inbound.ChannelId.ToString(),
            Text: stripped,
            Timestamp: inbound.Timestamp);
        return true;
    }

    public string Render(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsStreaming)
        {
            // Streaming responses are rendered incrementally by the adapter,
            // not by this synchronous helper.
            throw new InvalidOperationException(
                "Streaming responses cannot be rendered as a single string.");
        }

        var text = response.Text!;

        if (response.Citations.Count == 0)
        {
            return text;
        }

        var sb = new StringBuilder();
        sb.Append(text);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Sources:");
        foreach (var citation in response.Citations)
        {
            sb.Append("- ");
            sb.AppendLine(citation.ToString());
        }
        return sb.ToString().TrimEnd();
    }

    private static string StripBotMentions(string content, ulong botUserId)
    {
        var botIdText = botUserId.ToString();
        var result = MentionPattern().Replace(content, match =>
            match.Groups["id"].Value == botIdText ? string.Empty : match.Value);
        return result.Trim();
    }
}
