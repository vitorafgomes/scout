using System.Globalization;
using Microsoft.Extensions.Logging;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator;

/// <summary>
/// Measures the running experiments; the arms go live through the actions the owner approved, which
/// <see cref="ActionExecutor"/> runs, so this only reads. Each UTC day is credited to the arm live for most of its hours, so
/// a switch day never counts twice. Once an arm's window has ended and reach reports cover every day credited to it, the arm
/// gets its impressions and clicks from the reports and its average view percentage from the Analytics API; an ended arm
/// credited with no day counts zero. Nothing is saved when nothing changed.
/// Once every arm is measured, the experiment is decided with a pooled two-proportion z-test on the click-through rate of the
/// two best arms: a winner when z is at least <see cref="Significance"/>, every arm has at least <see cref="MinImpressions"/>
/// impressions and <see cref="MinClicks"/> clicks, and viewers watched the winner no more than <see cref="MaxViewDrop"/>
/// points less than the control; otherwise <c>inconclusive</c>. Nothing is adopted: that is the owner's choice.
/// </summary>
internal sealed class ExperimentRunner(
    IOperatorControlPlane controlPlane,
    IYouTubeAnalyticsClient analytics,
    ReachReports reach,
    TimeProvider time,
    ILogger<ExperimentRunner> logger)
{
    internal const long MinImpressions = 1000;
    internal const long MinClicks = 10;
    internal const double Significance = 1.96;
    internal const double MaxViewDrop = 5;
    internal const string Concluded = "concluded";
    internal const string Inconclusive = "inconclusive";
    internal const string Control = "control";

    /// <summary>Measure every running experiment of the latest projects and decide the ones whose arms are all measured.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var running = (await controlPlane.ListRecentAsync(StatusLimit, ct).ConfigureAwait(false))
            .Where(p => p.Publication is { VideoId: not null, Experiment.Status: ActionExecutor.Running })
            .ToList();
        var started = running.SelectMany(p => p.Publication!.Experiment!.Arms)
            .Where(a => a.EndedAt is not null)
            .Select(a => Parse(a.StartedAt))
            .OfType<DateTimeOffset>()
            .ToList();
        if (started.Count == 0)
        {
            return;
        }

        var data = await reach.LoadAsync(DateOnly.FromDateTime(started.Min().UtcDateTime), ct).ConfigureAwait(false);
        foreach (var project in running)
        {
            try
            {
                await MeasureAsync(project, data, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is YouTubeApiException or InvalidOperationException or HttpRequestException)
            {
                logger.LogWarning("Experiment of video project {ProjectId} could not be measured: {Reason}", project.Id, ex is HttpRequestException ? ex.GetType().Name : ex.Message);
            }
        }
    }

    internal const int StatusLimit = 50;

    private async Task MeasureAsync(OperatorProjectState project, ReachData? data, CancellationToken ct)
    {
        var publication = project.Publication!;
        var experiment = publication.Experiment!;
        var days = DaysOf(experiment.Arms, time.GetUtcNow());
        var measured = new List<OperatorExperimentArm>();
        var all = true;
        foreach (var arm in experiment.Arms)
        {
            var own = days.GetValueOrDefault(arm.Id) ?? [];
            if (arm.EndedAt is null)
            {
                measured.Add(arm);
                all = false;
                continue;
            }

            if (own.Count == 0)
            {
                measured.Add(arm with { Impressions = 0, Clicks = 0 });
                continue;
            }

            if (data?.Totals(publication.VideoId!, own) is not var (impressions, clicks))
            {
                measured.Add(arm);
                all = false;
                continue;
            }

            var report = await analytics.ReportAsync(publication.VideoId!, own[0], own[^1], ct).ConfigureAwait(false);
            measured.Add(arm with
            {
                Impressions = impressions,
                Clicks = clicks,
                AverageViewPercentage = report?.AverageViewPercentage ?? arm.AverageViewPercentage,
            });
        }

        var (winner, result) = all ? Decide(measured) : (null, null);
        var current = await controlPlane.GetProjectAsync(project.Id, ct).ConfigureAwait(false);
        if (current.Publication?.Experiment is not { Status: ActionExecutor.Running } live || live.Id != experiment.Id)
        {
            return;
        }

        var arms = live.Arms.Select(a => measured.FirstOrDefault(m => m.Id == a.Id) is { } m
            ? a with { Impressions = m.Impressions, Clicks = m.Clicks, AverageViewPercentage = m.AverageViewPercentage }
            : a).ToList();
        var next = live with { Arms = arms };
        if (result is not null)
        {
            next = next with { Status = winner is null ? Inconclusive : Concluded, WinnerArmId = winner, Result = result };
        }
        else if (arms.SequenceEqual(live.Arms))
        {
            return;
        }

        await controlPlane.SaveProjectAsync(current with { Publication = current.Publication with { Experiment = next } }, null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The UTC days each arm is credited with: a day goes to the arm live for most of its hours, the earlier arm on a tie, so a
    /// switch day counts for one arm only. A running arm is live until <paramref name="now"/>. Days in order.
    /// </summary>
    internal static Dictionary<string, List<DateOnly>> DaysOf(IReadOnlyList<OperatorExperimentArm> arms, DateTimeOffset now)
    {
        var live = arms
            .Select(a => (a.Id, From: Parse(a.StartedAt), To: Parse(a.EndedAt) ?? now))
            .Where(a => a.From is not null && a.From < a.To)
            .Select(a => (a.Id, From: a.From!.Value, a.To))
            .ToList();
        var days = arms.ToDictionary(a => a.Id, _ => new List<DateOnly>(), StringComparer.Ordinal);
        if (live.Count == 0)
        {
            return days;
        }

        for (var day = DateOnly.FromDateTime(live.Min(a => a.From).UtcDateTime); day <= DateOnly.FromDateTime(live.Max(a => a.To).UtcDateTime); day = day.AddDays(1))
        {
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var end = start.AddDays(1);
            var best = live
                .Select(a => (a.Id, Hours: (Min(a.To, end) - Max(a.From, start)).TotalHours))
                .Where(a => a.Hours > 0)
                .OrderByDescending(a => a.Hours)
                .FirstOrDefault();
            if (best.Id is not null)
            {
                days[best.Id].Add(day);
            }
        }

        return days;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset? Parse(string? at)
        => DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    /// <summary>The winning arm's id, or null, and the result in a sentence for the owner.</summary>
    internal static (string? Winner, string Result) Decide(IReadOnlyList<OperatorExperimentArm> arms)
    {
        if (arms.Count < 2 || arms.Any(a => a.Impressions < MinImpressions || a.Clicks < MinClicks))
        {
            return (null, "Inconclusive: every arm needs at least 1,000 impressions and 10 clicks.");
        }

        var ordered = arms.OrderByDescending(Ctr).ToList();
        var (best, next) = (ordered[0], ordered[1]);
        var pooled = (double)(best.Clicks + next.Clicks) / (best.Impressions + next.Impressions);
        var error = Math.Sqrt(pooled * (1 - pooled) * ((1.0 / best.Impressions) + (1.0 / next.Impressions)));
        var z = error == 0 ? 0 : (Ctr(best) - Ctr(next)) / error;
        var lead = string.Create(
            CultureInfo.InvariantCulture,
            $"{best.Label} has a {Ctr(best) * 100:0.0}% click-through rate against {Ctr(next) * 100:0.0}% for {next.Label} (z = {z:0.00})");
        if (z < Significance)
        {
            return (null, $"Inconclusive: {lead}, below the 1.96 a winner needs.");
        }

        if (arms.FirstOrDefault(a => a.Id == Control) is { } control && best.Id != Control
            && best.AverageViewPercentage < control.AverageViewPercentage - MaxViewDrop)
        {
            return (null, string.Create(
                CultureInfo.InvariantCulture,
                $"Inconclusive: {lead}, but viewers watched {best.AverageViewPercentage:0.0}% of it against {control.AverageViewPercentage:0.0}% for the control, more than 5 points less."));
        }

        return (best.Id, $"{lead}: {best.Label} wins.");
    }

    private static double Ctr(OperatorExperimentArm arm) => arm.Impressions == 0 ? 0 : (double)arm.Clicks / arm.Impressions;
}
