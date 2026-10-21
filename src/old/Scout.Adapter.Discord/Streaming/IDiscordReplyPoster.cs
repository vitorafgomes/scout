using Discord;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord.Streaming;

/// <summary>
/// Posts a Scout <see cref="ChatResponse"/> into a Discord text channel.
/// Plain responses become a single SendMessageAsync call; streaming responses
/// post an initial embed and edit it incrementally as chunks arrive, throttled
/// to respect Discord's edit rate limits. The citation footer is appended
/// after the body in both modes.
/// </summary>
public interface IDiscordReplyPoster
{
    /// <summary>
    /// Sends a Scout response into the given Discord text channel. For plain
    /// responses, posts a single message via SendMessageAsync. For streaming
    /// responses, posts an initial embed with the cursor and edits it as
    /// chunks arrive, throttling the edits to respect Discord rate limits.
    /// Citation footer is appended after the body in both modes.
    /// </summary>
    Task PostAsync(
        IMessageChannel channel,
        ChatResponse response,
        CancellationToken cancellationToken);
}
