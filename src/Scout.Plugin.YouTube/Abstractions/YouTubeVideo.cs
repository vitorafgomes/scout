namespace Scout.Plugin.YouTube.Abstractions;

/// <summary>
/// A video's public statistics. A count is null when the channel hides it or disables comments.
/// </summary>
public sealed record YouTubeVideo(
    string Id,
    string Title,
    string ChannelTitle,
    DateTimeOffset PublishedAt,
    TimeSpan Duration,
    long? Views,
    long? Likes,
    long? Comments);
