namespace Scout.Plugin.YouTube.Abstractions;

/// <summary>
/// One video a YouTube search returned, as the search result's snippet describes it; it carries no statistics.
/// </summary>
public sealed record YouTubeSearchResult(
    string VideoId,
    string Title,
    string ChannelTitle,
    DateTimeOffset PublishedAt,
    string Description);
