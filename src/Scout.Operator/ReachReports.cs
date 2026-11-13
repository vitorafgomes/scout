using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Scout.Llm.Abstractions;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator;

/// <summary>The rows of the reach reports read in one run and the days they cover.</summary>
internal sealed record ReachData(IReadOnlyList<YouTubeReachRow> Rows, IReadOnlySet<DateOnly> Covered)
{
    /// <summary>
    /// A video's thumbnail impressions and clicks from <paramref name="from"/> to <paramref name="to"/>, both included, the
    /// clicks being each day's impressions times its click-through rate, rounded; null unless a report covers every day.
    /// </summary>
    public (long Impressions, long Clicks)? Totals(string videoId, DateOnly from, DateOnly to)
        => Totals(videoId, [.. Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays)]);

    /// <summary>A video's thumbnail impressions and clicks on <paramref name="days"/>; null unless a report covers every one.</summary>
    public (long Impressions, long Clicks)? Totals(string videoId, IReadOnlyCollection<DateOnly> days)
    {
        if (!days.All(Covered.Contains))
        {
            return null;
        }

        var rows = Rows.Where(r => r.VideoId == videoId && days.Contains(r.Date)).ToList();
        return (rows.Sum(r => r.Impressions), rows.Sum(r => (long)Math.Round(r.Impressions * r.ImpressionsCtr, MidpointRounding.AwayFromZero)));
    }
}

/// <summary>
/// Reads the YouTube reach reports, the only source of thumbnail impressions and click-through rate. The reporting job is
/// created once, as a write the control plane allows under the owner's YouTube grant and records; until it is allowed, or
/// while YouTube cannot be read, there is no reach data and snapshots go without impressions. A report Scout cannot read is
/// left out and its day counts as not covered. A day YouTube reported again counts only its latest report, and a report is
/// downloaded once and kept while YouTube still lists it.
/// </summary>
internal sealed class ReachReports(IYouTubeReportingClient reporting, IWriteGate gate, ILogger<ReachReports> logger)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<YouTubeReachRow>> _downloaded = new(StringComparer.Ordinal);

    internal const string JobScope = "youtubeReporting jobs.create";

    internal const string JobTarget = "channel_reach_basic_a1";

    internal const string JobSummary =
        "Create the YouTube reach reporting job (channel_reach_basic_a1) so Scout can read thumbnail impressions and click-through rate.";

    /// <summary>The reach rows of the reports created since <paramref name="since"/>; null when there is no reach data to read.</summary>
    public async Task<ReachData?> LoadAsync(DateOnly since, CancellationToken ct)
    {
        try
        {
            var jobId = await reporting.FindReachJobAsync(ct).ConfigureAwait(false) ?? await CreateJobAsync(ct).ConfigureAwait(false);
            if (jobId is null)
            {
                return null;
            }

            var reports = await reporting.ListReportsAsync(jobId, new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), ct)
                .ConfigureAwait(false);
            var latest = reports
                .GroupBy(r => r.StartTime)
                .Select(day => day.MaxBy(r => r.CreateTime ?? DateTimeOffset.MinValue)!)
                .ToList();
            foreach (var stale in _downloaded.Keys.Except(latest.Select(r => r.Id)))
            {
                _downloaded.TryRemove(stale, out _);
            }

            var rows = new List<YouTubeReachRow>();
            var covered = new HashSet<DateOnly>();
            foreach (var report in latest)
            {
                try
                {
                    if (!_downloaded.TryGetValue(report.Id, out var read))
                    {
                        read = await reporting.DownloadReachAsync(report.DownloadUrl, ct).ConfigureAwait(false);
                        _downloaded[report.Id] = read;
                    }

                    rows.AddRange(read);
                    covered.Add(DateOnly.FromDateTime(report.StartTime.UtcDateTime));
                }
                catch (YouTubeApiException ex)
                {
                    logger.LogWarning("Reach report {ReportId} was left out: {Reason}", report.Id, ex.Message);
                }
            }

            return new ReachData(rows, covered);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is YouTubeApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning("YouTube reach reports could not be read: {Reason}", ex is YouTubeApiException or InvalidOperationException ? ex.Message : ex.GetType().Name);
            return null;
        }
    }

    private async Task<string?> CreateJobAsync(CancellationToken ct)
    {
        var decision = await gate.RequestAsync(ActionExecutor.FeatureId, JobScope, JobTarget, JobSummary, ct).ConfigureAwait(false);
        if (decision.Kind != WriteGateDecisionKind.Allowed)
        {
            logger.LogInformation("YouTube reach reports are off: {Reason}", decision.Reason);
            return null;
        }

        var outcome = "succeeded";
        try
        {
            return await reporting.CreateReachJobAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = $"failed: {ex.GetType().Name}";
            throw;
        }
        finally
        {
            try
            {
                await gate.RecordAsync(ActionExecutor.FeatureId, JobScope, JobTarget, outcome, decision.ActionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning("The reach reporting job could not be recorded ({ExceptionType}).", ex.GetType().Name);
            }
        }
    }
}
