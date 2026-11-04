using System.Text.RegularExpressions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord;

/// <summary>
/// Pure translator from Discord message payloads to the Scout
/// <see cref="ChatMessage"/>. No Discord.Net types cross this boundary, so the
/// translator can be unit-tested without mocking SDK primitives. Outbound
/// rendering is owned by <c>DiscordResponseSink</c>, so this type only decides
/// whether an inbound message is for Scout and normalizes it. A voice message
/// becomes a <see cref="ChatMessage"/> with an <see cref="AudioAttachment"/>
/// whose download runs only when the core asks for it.
/// </summary>
public sealed partial class DiscordMessageTranslator
{
    private readonly Func<string, CancellationToken, Task<Stream>> _download;

    /// <summary>
    /// Creates the translator. <paramref name="download"/> opens an attachment URL;
    /// by default an HTTPS GET.
    /// </summary>
    public DiscordMessageTranslator(Func<string, CancellationToken, Task<Stream>>? download = null)
    {
        _download = download
            ?? ((url, ct) => DiscordAttachmentDownloader.OpenAsync(DiscordAttachmentDownloader.Shared, url, ct));
    }

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

        // In a guild channel the bot must be explicitly @-mentioned so it does
        // not respond to unrelated traffic. DMs (no GuildId) are implicitly
        // addressed to the bot and do not require a mention. A voice message
        // cannot carry a mention, so a voice reply to the bot counts as one.
        var isDm = inbound.GuildId is null;
        var addressed = inbound.MentionsSelf || (inbound.Voice is not null && inbound.RepliesToSelf);
        if (!isDm && !addressed)
        {
            reason = "no-mention";
            return false;
        }

        if (inbound.Voice is { } voice)
        {
            chatMessage = new ChatMessage(
                UserId: inbound.AuthorId.ToString(),
                ChannelId: inbound.ChannelId.ToString(),
                Text: string.Empty,
                Timestamp: inbound.Timestamp,
                Audio: new AudioAttachment(
                    voice.ContentType,
                    voice.SizeBytes,
                    voice.Duration,
                    ct => _download(voice.Url, ct)),
                IsDirectMessage: isDm)
            { OriginReference = DiscordOriginPoster.Reference(inbound.ChannelId, inbound.MessageId) };
            return true;
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
            Timestamp: inbound.Timestamp,
            IsDirectMessage: isDm)
        { OriginReference = DiscordOriginPoster.Reference(inbound.ChannelId, inbound.MessageId) };
        return true;
    }

    private static string StripBotMentions(string content, ulong botUserId)
    {
        var botIdText = botUserId.ToString();
        var result = MentionPattern().Replace(content, match =>
            match.Groups["id"].Value == botIdText ? string.Empty : match.Value);
        return result.Trim();
    }
}
