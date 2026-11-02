namespace Scout.Plugin.YouTube.Abstractions;

/// <summary>
/// A channel's public summary. <see cref="Subscribers"/> is null when the channel hides it.
/// </summary>
public sealed record YouTubeChannel(
    string Id,
    string Title,
    string? Handle,
    long? Subscribers,
    long Views,
    long VideoCount,
    DateTimeOffset PublishedAt,
    string? UploadsPlaylistId);
