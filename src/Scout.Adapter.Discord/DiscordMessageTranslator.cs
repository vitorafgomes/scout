using System.Text;
using System.Text.RegularExpressions;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord;

/// <summary>
/// Pure translator between Discord message payloads and the Scout core DTOs.
/// No Discord.Net types cross this boundary, so the translator can be unit-
/// tested without mocking SDK primitives.
///
/// - <see cref="TryTranslate"/> decides whether an inbound Discord message is
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
    {
        ArgumentNullException.ThrowIfNull(inbound);

        chatMessage = null;

        if (inbound.AuthorId == botUserId)
        {
            return false;
        }
        if (!inbound.MentionsSelf)
        {
            return false;
        }

        var stripped = StripBotMentions(inbound.Content ?? string.Empty, botUserId);
        if (string.IsNullOrWhiteSpace(stripped))
        {
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

        if (response.Citations.Count == 0)
        {
            return response.Text;
        }

        var sb = new StringBuilder();
        sb.Append(response.Text);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Sources:");
        foreach (var citation in response.Citations)
        {
            sb.Append("- ");
            sb.AppendLine(citation);
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
