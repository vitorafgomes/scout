using Microsoft.Extensions.Logging;
using Scout.Adapter.Slack.Streaming;
using Scout.Conversation.Abstractions;
using SlackNet.WebApi;

namespace Scout.Adapter.Slack;

/// <summary>
/// Posts a later plain-text message into the Slack thread a reference names. The payload is
/// <c>&lt;channel id&gt;:&lt;thread ts&gt;</c>, the same channel and thread the turn's reply went to.
/// </summary>
public sealed class SlackOriginPoster(SlackChatAdapter adapter, ILogger<SlackOriginPoster> logger) : IOriginPoster
{
    internal const string Prefix = "slack";

    /// <inheritdoc />
    public string Kind => Prefix;

    internal static string Reference(string channelId, string threadTs) => $"{Prefix}:{channelId}:{threadTs}";

    private static string Escape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<bool> PostAsync(string payload, string text, CancellationToken ct)
    {
        var parts = payload.Split(':');
        if (parts is not [{ Length: > 0 } channelId, { Length: > 0 } threadTs])
        {
            logger.LogWarning("A Slack origin reference could not be read; nothing was posted.");
            return false;
        }
        if (adapter.RunningApiClient is not { } client)
        {
            logger.LogWarning("The Slack adapter is not running; nothing was posted to the originating conversation.");
            return false;
        }

        try
        {
            await client.Chat.PostMessage(
                    new Message
                    {
                        Channel = channelId,
                        ThreadTs = threadTs,
                        Text = Escape(text.Length > SlackResponseSink.MaxBodyLength
                            ? text[..(SlackResponseSink.MaxBodyLength - 3)] + "..."
                            : text),
                    },
                    ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Slack refused a post to the originating conversation.");
            return false;
        }
    }
}
