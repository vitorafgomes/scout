using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Plugin.YouTube;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Operator;

/// <summary>
/// Learns from the owner's published videos, first saving the scheduled ones that went live as published. Once a video's first <c>24h</c> or <c>7d</c> have passed, counted from the
/// scheduled publish time or else the upload, plus <see cref="Latency"/> for YouTube's data to settle, it takes a snapshot
/// of the window from the Analytics API, with retention and, when reach reports cover the window, impressions and their
/// click-through rate, and saves it. Then it asks the chat model, offered no function, for up to three proposals for that
/// video (a title, a thumbnail picture, the next topic) against the channel's earlier snapshots of the same window; a
/// thumbnail proposal gets its picture from the image service, counted in the day's pictures, and is left out when no picture
/// was made; a video whose proposals fail is logged and the next one still gets its own. It also proposes the next
/// topic when the mandate's cadence says one is due, and once a week a written review. Every proposal is saved
/// <c>proposed</c> under an id made from its window or date, so a run repeated after a restart adds nothing twice. Nothing is
/// stored beyond what the APIs return; proposals are text.
/// </summary>
internal sealed class LearningLoop(
    IOperatorControlPlane controlPlane,
    IYouTubeAnalyticsClient analytics,
    ReachReports reach,
    IChatCompletionService chat,
    VisualGenerator visuals,
    PublishedCheck published,
    TimeProvider time,
    ILogger<LearningLoop> logger)
{
    internal static readonly TimeSpan Latency = TimeSpan.FromHours(72);

    internal static readonly IReadOnlyList<(string Name, TimeSpan Length)> Windows =
        [("24h", TimeSpan.FromDays(1)), ("7d", TimeSpan.FromDays(7))];

    internal const int ProjectLimit = 50;
    internal const int MaxProposals = 3;
    internal const string Proposed = "proposed";

    internal const string InvalidAnswer = "The AI model did not return proposals in the expected form, twice.";

    /// <summary>Take every snapshot that is due, and propose changes for each video that got one.</summary>
    public async Task SnapshotAsync(CancellationToken ct)
    {
        await published.RunAsync(ct).ConfigureAwait(false);
        var projects = await controlPlane.ListRecentAsync(ProjectLimit, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var due = projects
            .Select(p => (Project: p, Start: Start(p)))
            .Where(d => d.Start is not null)
            .SelectMany(d => Windows
                .Where(w => now >= d.Start!.Value + w.Length + Latency && d.Project.Publication!.Snapshots.All(s => s.Window != w.Name))
                .Select(w => (d.Project, Start: d.Start!.Value, Window: w)))
            .ToList();
        if (due.Count == 0)
        {
            return;
        }

        var data = await reach.LoadAsync(Day(due.Min(d => d.Start)), ct).ConfigureAwait(false);
        foreach (var (project, start, window) in due)
        {
            try
            {
                var saved = await TakeAsync(project, start, window, data, ct).ConfigureAwait(false);
                if (saved is not null)
                {
                    await ProposeForAsync(saved, window.Name, projects, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is YouTubeApiException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(
                    "The {Window} snapshot of video project {ProjectId} was not taken: {Reason}",
                    window.Name, project.Id, ex is YouTubeApiException or InvalidOperationException ? ex.Message : ex.GetType().Name);
            }
        }
    }

    /// <summary>Propose changes for every video whose latest snapshot has no proposals yet.</summary>
    public async Task ProposeAsync(CancellationToken ct)
    {
        var projects = await controlPlane.ListRecentAsync(ProjectLimit, ct).ConfigureAwait(false);
        foreach (var project in projects)
        {
            if (project.Publication is not { Snapshots.Count: > 0 } publication)
            {
                continue;
            }

            var window = publication.Snapshots[^1].Window;
            if (publication.Proposals.Any(p => p.Id.StartsWith(window + "-", StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                await ProposeForAsync(project, window, projects, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("No proposals for video project {ProjectId} this time: {Reason}", project.Id, OperatorPipeline.Reason(ex));
            }
        }
    }

    /// <summary>Propose the next topic when the mandate's cadence says one is due and no project started within it.</summary>
    public async Task ProposeNextTopicAsync(CancellationToken ct)
    {
        var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
        if (Cadence(mandate.Cadence) is not { } every)
        {
            return;
        }

        var projects = await controlPlane.ListRecentAsync(ProjectLimit, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (projects.Any(p => Parse(p.CreatedAt) is { } created && now - created < every))
        {
            return;
        }

        await ProposeOnLatestAsync(
            projects,
            $"topic-{LondonDay(now):yyyy-MM-dd}",
            "topic",
            "Propose the next video topic for the channel, one proposal of kind \"topic\".",
            mandate,
            ct).ConfigureAwait(false);
    }

    /// <summary>Write the week's review as one proposal of kind <c>strategy</c>.</summary>
    public async Task ReviewWeekAsync(CancellationToken ct)
    {
        var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
        var projects = await controlPlane.ListRecentAsync(ProjectLimit, ct).ConfigureAwait(false);
        await ProposeOnLatestAsync(
            projects,
            $"strategy-{LondonDay(time.GetUtcNow()):yyyy-MM-dd}",
            "strategy",
            "Review the channel's week in one proposal of kind \"strategy\": what worked, what did not, and what to do next, in at most 120 words.",
            mandate,
            ct).ConfigureAwait(false);
    }

    /// <summary>How often the mandate's cadence asks for a video; null when it names no daily, weekly or monthly rhythm.</summary>
    internal static TimeSpan? Cadence(string cadence)
    {
        var text = (cadence ?? string.Empty).ToUpperInvariant();
        return text.Contains("DAILY", StringComparison.Ordinal) || text.Contains("EVERY DAY", StringComparison.Ordinal) ? TimeSpan.FromDays(1)
            : text.Contains("WEEK", StringComparison.Ordinal) ? TimeSpan.FromDays(7)
            : text.Contains("MONTH", StringComparison.Ordinal) ? TimeSpan.FromDays(30)
            : null;
    }

    private async Task<OperatorProjectState?> TakeAsync(
        OperatorProjectState project, DateTimeOffset start, (string Name, TimeSpan Length) window, ReachData? data, CancellationToken ct)
    {
        var videoId = project.Publication!.VideoId!;
        var (from, to) = (Day(start), Day(start + window.Length));
        var report = await analytics.ReportAsync(videoId, from, to, ct).ConfigureAwait(false);
        if (report is null)
        {
            logger.LogInformation("YouTube has no {Window} data yet for video project {ProjectId}.", window.Name, project.Id);
            return null;
        }

        IReadOnlyList<YouTubeRetentionPoint> retention;
        try
        {
            retention = await analytics.RetentionAsync(videoId, from, to, ct).ConfigureAwait(false);
        }
        catch (YouTubeApiException ex)
        {
            logger.LogWarning("Retention of video project {ProjectId} was left out: {Reason}", project.Id, ex.Message);
            retention = [];
        }

        var reached = data?.Totals(videoId, from, to);
        var snapshot = new OperatorSnapshot(
            window.Name,
            ActionExecutor.Iso(time.GetUtcNow()),
            report.Views,
            report.MinutesWatched,
            report.AverageViewDurationSeconds,
            report.AverageViewPercentage,
            report.SubscribersGained,
            report.SubscribersLost,
            report.Likes,
            report.Comments,
            report.Shares,
            reached?.Impressions,
            reached is { Impressions: > 0 } r ? (double)r.Clicks / r.Impressions : null,
            [.. retention.Select(p => new OperatorRetentionPoint(p.ElapsedRatio, p.AudienceWatchRatio))]);
        var current = await controlPlane.GetProjectAsync(project.Id, ct).ConfigureAwait(false);
        if (current.Publication is not { } publication || publication.Snapshots.Any(s => s.Window == window.Name))
        {
            return null;
        }

        return await controlPlane.SaveProjectAsync(
            current with { Publication = publication with { Snapshots = [.. publication.Snapshots, snapshot] } }, null, ct).ConfigureAwait(false);
    }

    private async Task ProposeForAsync(OperatorProjectState project, string window, IReadOnlyList<OperatorProjectState> channel, CancellationToken ct)
    {
        var snapshot = project.Publication!.Snapshots.Last(s => s.Window == window);
        var history = channel
            .Where(p => p.Id != project.Id)
            .SelectMany(p => (p.Publication?.Snapshots ?? []).Where(s => s.Window == window).Select(s => $"- {p.Topic}: {Describe(s)}"))
            .ToList();
        var mandate = await controlPlane.GetMandateAsync(ct).ConfigureAwait(false);
        var request = $"""
            Video: {project.Topic}, titled "{project.Package?.Title ?? project.Topic}".
            Its first {window}: {Describe(snapshot)}.
            The channel's earlier videos over their first {window}:
            {(history.Count == 0 ? "- none yet" : string.Join("\n", history))}
            Propose at most {MaxProposals} changes: a new title, a thumbnail picture, or the next topic.
            """;
        var answered = await AskAsync(request, mandate, ct).ConfigureAwait(false);
        var proposals = new List<OperatorProposal>();
        foreach (var (item, n) in answered.Where(p => p.Kind is "title" or "thumbnail" or "topic").Take(MaxProposals).Select((p, i) => (p, i + 1)))
        {
            var id = $"{window}-{n}";
            var picture = item.Kind == "thumbnail" ? await PictureAsync(project.Id, id, item.Text!, ct).ConfigureAwait(false) : null;
            if (item.Kind == "thumbnail" && picture is null)
            {
                continue;
            }

            proposals.Add(new OperatorProposal(id, item.Kind!, item.Text!.Trim(), (item.Reason ?? string.Empty).Trim(), Proposed, picture));
        }

        await SaveProposalsAsync(project.Id, proposals, ct).ConfigureAwait(false);
    }

    private async Task ProposeOnLatestAsync(
        IReadOnlyList<OperatorProjectState> projects, string id, string kind, string ask, OperatorMandate mandate, CancellationToken ct)
    {
        if (projects.FirstOrDefault(p => p.Publication?.VideoId is not null) is not { } latest
            || latest.Publication!.Proposals.Any(p => p.Id == id))
        {
            return;
        }

        var lines = projects
            .Where(p => p.Publication?.Snapshots.Count > 0)
            .Select(p => $"- {p.Topic}: {Describe(p.Publication!.Snapshots[^1])} (first {p.Publication.Snapshots[^1].Window})")
            .ToList();
        var request = $"""
            Recent videos and their latest numbers:
            {(lines.Count == 0 ? "- none measured yet" : string.Join("\n", lines))}
            Recent topics: {string.Join("; ", projects.Take(10).Select(p => p.Topic))}.
            {ask}
            """;
        var answered = await AskAsync(request, mandate, ct).ConfigureAwait(false);
        if (answered.FirstOrDefault() is { } first)
        {
            await SaveProposalsAsync(latest.Id, [new OperatorProposal(id, kind, first.Text!.Trim(), (first.Reason ?? string.Empty).Trim(), Proposed, null)], ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<ProposalDto>> AskAsync(string request, OperatorMandate mandate, CancellationToken ct)
    {
        var language = string.IsNullOrWhiteSpace(mandate.Language) ? "English" : mandate.Language.Trim();
        var system = $$"""
            You help the owner of a YouTube channel decide what to change. The channel's objective: {{mandate.Objective}}. Audience: {{mandate.Audience}}.
            Its target: {{mandate.TargetMetric}} {{mandate.TargetValue}}. Never propose a topic about: {{string.Join(", ", mandate.BlockedTopics)}}.
            Answer with JSON only: {"proposals":[{"kind":"title|thumbnail|topic|strategy","text":"...","reason":"..."}]}.
            A title has at most 100 characters and no < or >. A thumbnail is a picture description with no text, letters, logos, trademarks or real people.
            Base every reason on the numbers given and never invent a number. Write in {{language}}.
            """;
        try
        {
            var dto = await ModelJson.AskAsync<ProposalsDto>(chat, system, request, d => d.Proposals is not null, InvalidAnswer, ct).ConfigureAwait(false);
            return [.. dto.Proposals!.Where(p => !string.IsNullOrWhiteSpace(p.Text) && !string.IsNullOrWhiteSpace(p.Kind))];
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("No proposals this time: {Reason}", ex.Message);
            return [];
        }
    }

    private async Task<string?> PictureAsync(string projectId, string proposalId, string prompt, CancellationToken ct)
    {
        var file = $"thumbnail-proposal-{proposalId}.png";
        try
        {
            var directory = await controlPlane.GetMediaDirectoryAsync(projectId, ct).ConfigureAwait(false);
            return await visuals.PictureAsync(
                $"{prompt.Trim()} A YouTube thumbnail picture: no text, no letters, no logos, no trademarks, no real person's likeness and no public figure.",
                directory,
                file,
                ct).ConfigureAwait(false) ? file : null;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("The picture of thumbnail proposal {ProposalId} was not made: {Reason}", proposalId, ex.Message);
            return null;
        }
    }

    private async Task SaveProposalsAsync(string projectId, IReadOnlyList<OperatorProposal> proposals, CancellationToken ct)
    {
        if (proposals.Count == 0)
        {
            return;
        }

        var current = await controlPlane.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        if (current.Publication is not { } publication)
        {
            return;
        }

        var fresh = proposals.Where(p => publication.Proposals.All(existing => existing.Id != p.Id)).ToList();
        if (fresh.Count > 0)
        {
            await controlPlane.SaveProjectAsync(
                current with { Publication = publication with { Proposals = [.. publication.Proposals, .. fresh] } }, null, ct).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset? Start(OperatorProjectState project)
        => project.Publication is { VideoId: not null, Status: ActionExecutor.Published or ActionExecutor.Scheduled } publication
            ? Parse(publication.PublishAt) ?? Parse(publication.UploadedAt)
            : null;

    private static DateTimeOffset? Parse(string? at)
        => DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);

    private static DateOnly LondonDay(DateTimeOffset at) => DateOnly.FromDateTime(London.Of(at).DateTime);

    internal static string Describe(OperatorSnapshot s)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{s.Views:N0} views, {s.MinutesWatched:N0} minutes watched, {s.AverageViewPercentage:0.0}% average view percentage, {s.SubscribersGained - s.SubscribersLost:+#;-#;0} subscribers, {s.Likes:N0} likes, {s.Comments:N0} comments, {s.Shares:N0} shares");
        return s is { Impressions: { } impressions, ImpressionsCtr: { } ctr }
            ? string.Create(CultureInfo.InvariantCulture, $"{line}, {impressions:N0} impressions at {ctr * 100:0.0}% click-through rate")
            : line;
    }

    private sealed record ProposalsDto(List<ProposalDto>? Proposals);

    private sealed record ProposalDto(string? Kind, string? Text, string? Reason);
}
