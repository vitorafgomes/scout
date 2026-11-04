namespace Scout.Plugin.YouTube.Abstractions;

/// <summary>
/// The YouTube Reporting API's daily reach reports (<c>channel_reach_basic_a1</c>), the only source of thumbnail
/// impressions and their click-through rate.
/// </summary>
public interface IYouTubeReportingClient
{
    /// <summary>The id of the owner's reach reporting job; null when none exists.</summary>
    Task<string?> FindReachJobAsync(CancellationToken ct);

    /// <summary>Create the reach reporting job (<c>jobs.create</c>, a write on YouTube); its id.</summary>
    Task<string> CreateReachJobAsync(CancellationToken ct);

    /// <summary>The reports of <paramref name="jobId"/> created after <paramref name="createdAfter"/>.</summary>
    Task<IReadOnlyList<YouTubeReport>> ListReportsAsync(string jobId, DateTimeOffset createdAfter, CancellationToken ct);

    /// <summary>Download one reach report and read its rows; refuses an address outside the Reporting API.</summary>
    Task<IReadOnlyList<YouTubeReachRow>> DownloadReachAsync(string downloadUrl, CancellationToken ct);
}

/// <summary>
/// One daily report: the day it covers, from <see cref="StartTime"/> to <see cref="EndTime"/>, where to download it, and when
/// YouTube made it; a day YouTube reported again has a later <see cref="CreateTime"/>.
/// </summary>
public sealed record YouTubeReport(string Id, DateTimeOffset StartTime, DateTimeOffset EndTime, string DownloadUrl, DateTimeOffset? CreateTime = null);

/// <summary>
/// One row of a reach report: a video's thumbnail impressions on a day and their click-through rate as a fraction from 0
/// to 1, whether the report gave a fraction or a percent.
/// </summary>
public sealed record YouTubeReachRow(DateOnly Date, string VideoId, long Impressions, double ImpressionsCtr);
