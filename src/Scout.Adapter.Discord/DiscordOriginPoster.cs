using System.Globalization;
using Discord;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord;

/// <summary>
/// Posts a later plain-text message into the Discord channel, direct message or thread a reference names.
/// The payload is <c>&lt;channel id&gt;:&lt;message id&gt;</c>, and the post replies to that message, or
/// <c>&lt;thread id&gt;</c> for a conversation Scout moved into a thread. Mentions never ping.
/// </summary>
public sealed class DiscordOriginPoster : IOriginPoster
{
    internal const int MaxContentLength = 2000;
    internal const string Prefix = "discord";

    private readonly Func<ulong, CancellationToken, Task<IMessageChannel?>> _findChannel;
    private readonly ILogger _logger;

    /// <summary>Resolves channels through the running <see cref="DiscordChatAdapter"/>.</summary>
    public DiscordOriginPoster(DiscordChatAdapter adapter, ILogger<DiscordOriginPoster> logger)
        : this(adapter.FindChannelAsync, logger)
    {
    }

    internal DiscordOriginPoster(Func<ulong, CancellationToken, Task<IMessageChannel?>> findChannel, ILogger logger)
    {
        _findChannel = findChannel ?? throw new ArgumentNullException(nameof(findChannel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Kind => Prefix;

    internal static string Reference(ulong channelId, ulong? messageId = null)
        => messageId is { } id
            ? string.Create(CultureInfo.InvariantCulture, $"{Prefix}:{channelId}:{id}")
            : string.Create(CultureInfo.InvariantCulture, $"{Prefix}:{channelId}");

    /// <inheritdoc />
    public async Task<bool> PostAsync(string payload, string text, CancellationToken ct)
    {
        var parts = payload.Split(':');
        ulong messageId = 0;
        if (parts.Length > 2
            || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var channelId)
            || (parts.Length == 2 && !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out messageId)))
        {
            _logger.LogWarning("A Discord origin reference could not be read; nothing was posted.");
            return false;
        }

        try
        {
            if (await _findChannel(channelId, ct).ConfigureAwait(false) is not { } channel)
            {
                _logger.LogWarning("The originating Discord conversation is not reachable; nothing was posted.");
                return false;
            }

            var replyTo = parts.Length == 2
                ? new MessageReference(messageId, channelId, failIfNotExists: false)
                : null;
            await channel.SendMessageAsync(
                    text: text.Length > MaxContentLength ? text[..(MaxContentLength - 3)] + "..." : text,
                    options: new RequestOptions { CancelToken = ct },
                    allowedMentions: AllowedMentions.None,
                    messageReference: replyTo)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Discord refused a post to the originating conversation.");
            return false;
        }
    }
}
