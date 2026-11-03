namespace Scout.Plugin.YouTube.Abstractions;

/// <summary>The YouTube Analytics API v2 reports of the signed-in owner's own videos.</summary>
public interface IYouTubeAnalyticsClient
{
    /// <summary>One video's totals from <paramref name="start"/> to <paramref name="end"/>, both included; null when YouTube has no data for them yet.</summary>
    Task<YouTubeVideoReport?> ReportAsync(string videoId, DateOnly start, DateOnly end, CancellationToken ct);

    /// <summary>One video's audience retention from <paramref name="start"/> to <paramref name="end"/>, by elapsed share of the video.</summary>
    Task<IReadOnlyList<YouTubeRetentionPoint>> RetentionAsync(string videoId, DateOnly start, DateOnly end, CancellationToken ct);
}

/// <summary>A video's totals as the Analytics API reports them; minutes and seconds as YouTube returns them.</summary>
public sealed record YouTubeVideoReport(
    long Views,
    double MinutesWatched,
    double AverageViewDurationSeconds,
    double AverageViewPercentage,
    long SubscribersGained,
    long SubscribersLost,
    long Likes,
    long Comments,
    long Shares);

/// <summary>The share of the audience still watching (<see cref="AudienceWatchRatio"/>) at a share of the video's length.</summary>
public sealed record YouTubeRetentionPoint(double ElapsedRatio, double AudienceWatchRatio);
