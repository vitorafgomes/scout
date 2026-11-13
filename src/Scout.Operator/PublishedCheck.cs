using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator;

/// <summary>
/// Saves a scheduled video as <c>published</c> once its publish time has passed and YouTube shows it public or unlisted.
/// Each video is asked about at most every <see cref="Every"/>; a call that fails is logged and the video stays
/// <c>scheduled</c> until the next check.
/// </summary>
internal sealed class PublishedCheck(
    IOperatorControlPlane controlPlane,
    IYouTubeWriteClient youtube,
    TimeProvider time,
    ILogger<PublishedCheck> logger)
{
    internal static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _checked = new(StringComparer.Ordinal);

    /// <summary>Check every scheduled video of the latest projects whose publish time has passed.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        IReadOnlyList<OperatorProjectState> projects;
        try
        {
            projects = await controlPlane.ListRecentAsync(LearningLoop.ProjectLimit, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (var project in projects)
        {
            if (project.Publication is not { Status: ActionExecutor.Scheduled, VideoId: { } videoId, PublishAt: { } publishAt }
                || !DateTimeOffset.TryParse(publishAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                || at > now
                || (_checked.TryGetValue(project.Id, out var last) && now - last < Every))
            {
                continue;
            }

            _checked[project.Id] = now;
            try
            {
                var status = await youtube.GetVideoStatusAsync(videoId, ct).ConfigureAwait(false);
                if (status?.PrivacyStatus is not ("public" or "unlisted"))
                {
                    continue;
                }

                var current = await controlPlane.GetProjectAsync(project.Id, ct).ConfigureAwait(false);
                if (current.Publication is { Status: ActionExecutor.Scheduled } publication)
                {
                    await controlPlane.SaveProjectAsync(current with { Publication = publication with { Status = ActionExecutor.Published } }, null, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is YouTubeApiException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(
                    "Whether video project {ProjectId} went live could not be checked: {Reason}",
                    project.Id, ex is HttpRequestException or TaskCanceledException ? ex.GetType().Name : ex.Message);
            }
        }
    }
}
