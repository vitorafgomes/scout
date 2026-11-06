using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Plugin.YouTube;

namespace Scout.Operator;

/// <summary>
/// One job of the operator's schedule: its name, the latest slot at or before a time, whether the slot is a time of day
/// on the London clock, and whether it needs the owner's YouTube sign-in.
/// </summary>
internal sealed record ScheduledJob(
    string Name,
    Func<DateTimeOffset, DateTimeOffset> LatestSlot,
    bool OnTheClock,
    bool NeedsYouTube,
    Func<CancellationToken, Task> Run)
{
    /// <summary>Every day at <paramref name="hour"/>:00 London time.</summary>
    public static ScheduledJob Daily(string name, int hour, bool needsYouTube, Func<CancellationToken, Task> run)
        => new($"{name} {hour:00}:00", now => At(now, hour, _ => true), OnTheClock: true, needsYouTube, run);

    /// <summary>Every <paramref name="day"/> at <paramref name="hour"/>:00 London time.</summary>
    public static ScheduledJob Weekly(string name, DayOfWeek day, int hour, bool needsYouTube, Func<CancellationToken, Task> run)
        => new($"{name} {day} {hour:00}:00", now => At(now, hour, d => d.DayOfWeek == day), OnTheClock: true, needsYouTube, run);

    /// <summary>Every <paramref name="period"/>, on the UTC boundaries of that period, and once at start.</summary>
    public static ScheduledJob Every(string name, TimeSpan period, bool needsYouTube, Func<CancellationToken, Task> run)
        => new(name, now => new DateTimeOffset(now.UtcTicks - (now.UtcTicks % period.Ticks), TimeSpan.Zero), OnTheClock: false, needsYouTube, run);

    /// <summary>The latest <paramref name="hour"/>:00 on the London clock at or before <paramref name="now"/>, on a day <paramref name="matches"/> accepts.</summary>
    internal static DateTimeOffset At(DateTimeOffset now, int hour, Func<DateTime, bool> matches)
    {
        var local = London.Of(now).DateTime.Date;
        for (var day = local; ; day = day.AddDays(-1))
        {
            var slot = day.AddHours(hour);
            var at = new DateTimeOffset(slot, London.Zone.GetUtcOffset(slot));
            if (matches(day) && at <= now)
            {
                return at;
            }
        }
    }
}

/// <summary>The note the scheduler leaves for the readiness report about slots it ran late.</summary>
internal sealed class ScheduleNotes
{
    private volatile string? _missed;

    /// <summary>The jobs whose slot passed while Scout was not running and ran when it started; null when none.</summary>
    public string? Missed
    {
        get => _missed;
        set => _missed = value;
    }
}

/// <summary>
/// Runs the operator's jobs on the London clock, checking every <see cref="Tick"/>: each job once per slot. The slot each
/// job last ran is kept in <c>operator-schedule.json</c> in Scout's data folder, beside the operator's media root, so a
/// restart knows what ran: at start a job runs only when its stored last run is before its latest slot, and the slots on
/// the clock it so catches up are named in the readiness notes; a job with no stored run, as on the first start ever, waits
/// for its next slot. Until the state file can be found, before the first project exists or while the control plane cannot
/// be reached, nothing runs. An unreadable state file counts as empty and is logged. A job that needs YouTube is skipped while
/// the owner is not signed in. A job that fails is logged and the others go on.
/// </summary>
internal sealed class OperatorScheduler(
    IEnumerable<ScheduledJob> jobs,
    ScheduleNotes notes,
    IOptions<YouTubeOptions> youtube,
    IOperatorControlPlane controlPlane,
    TimeProvider time,
    ILogger<OperatorScheduler> logger) : BackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    internal const string StateFileName = "operator-schedule.json";

    internal const string Unreadable = "The operator's schedule could not be read, so no missed slot runs at start.";

    private readonly List<ScheduledJob> _jobs = [.. jobs];
    private readonly Dictionary<string, DateTimeOffset> _done = new(StringComparer.Ordinal);
    private string? _stateFile;
    private bool _stateLoaded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            await RunDueAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunDueAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Run every job whose latest slot has not run yet.</summary>
    internal async Task RunDueAsync(CancellationToken ct)
    {
        var starting = !_stateLoaded;
        if (starting)
        {
            if (await StateFileAsync(ct).ConfigureAwait(false) is null)
            {
                return;
            }

            foreach (var (name, at) in await ReadStateAsync(ct).ConfigureAwait(false))
            {
                _done[name] = at;
            }

            _stateLoaded = true;
        }

        var now = time.GetUtcNow();

        var tokenFile = youtube.Value.TokenFile;
        var signedIn = !string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile);
        var late = new List<string>();
        var changed = false;
        foreach (var job in _jobs)
        {
            var slot = job.LatestSlot(now);
            var known = _done.TryGetValue(job.Name, out var done);
            if (known && done >= slot)
            {
                continue;
            }

            if (!known)
            {
                _done[job.Name] = slot;
                changed = true;
                continue;
            }

            if (job.NeedsYouTube && !signedIn)
            {
                continue;
            }

            _done[job.Name] = slot;
            changed = true;
            if (starting && job.OnTheClock)
            {
                late.Add(job.Name);
            }

            try
            {
                await job.Run(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("The operator's {Job} job failed: {Reason}", job.Name, OperatorPipeline.Reason(ex));
            }
        }

        if (starting)
        {
            notes.Missed = late.Count == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"Scout was not running for these scheduled jobs and ran them when it started: {string.Join(", ", late)}.");
        }

        if (changed)
        {
            await WriteStateAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, DateTimeOffset>> ReadStateAsync(CancellationToken ct)
    {
        var file = await StateFileAsync(ct).ConfigureAwait(false);
        if (file is null || !File.Exists(file))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, DateTimeOffset>>(stream, cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(Unreadable);
            return [];
        }
    }

    private async Task WriteStateAsync(CancellationToken ct)
    {
        var file = await StateFileAsync(ct).ConfigureAwait(false);
        if (file is null)
        {
            return;
        }

        var temporary = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_done), ct).ConfigureAwait(false);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            logger.LogWarning("The operator's schedule could not be saved ({ExceptionType}); a restart may report slots as missed.", ex.GetType().Name);
        }
    }

    private async Task<string?> StateFileAsync(CancellationToken ct)
    {
        if (_stateFile is not null)
        {
            return _stateFile;
        }

        try
        {
            var recent = await controlPlane.ListRecentAsync(1, ct).ConfigureAwait(false);
            var project = recent.Count > 0 ? recent[0] : null;
            if (project is null)
            {
                return null;
            }

            var media = await controlPlane.GetMediaDirectoryAsync(project.Id, ct).ConfigureAwait(false);
            var dataFolder = Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(media)));
            _stateFile = string.IsNullOrEmpty(dataFolder) ? null : Path.Combine(dataFolder, StateFileName);
            return _stateFile;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
