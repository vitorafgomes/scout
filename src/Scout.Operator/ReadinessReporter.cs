using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Llm.Abstractions;
using Scout.Plugin.YouTube;

namespace Scout.Operator;

/// <summary>
/// Tells the control plane what this worker can produce, when it starts and every <see cref="Interval"/>: speech and
/// images as the connected provider's media services allow, the provider's name, whether videos can be assembled, with a note naming the encoder or why not, whether the
/// YouTube token file the desktop writes at sign-in is on disk, and a note for each thing the owner has to supply.
/// A control plane that cannot take the report is logged once until it takes one again.
/// </summary>
internal sealed class ReadinessReporter(
    IOperatorControlPlane controlPlane,
    MediaCapabilities media,
    IOptions<YouTubeOptions> youtube,
    TimeProvider time,
    ILogger<ReadinessReporter> logger,
    FfmpegToolset ffmpeg,
    ScheduleNotes? schedule = null) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    internal const string NotSignedIn = "Scout is not signed in to YouTube; research goes on without the channel's own uploads.";

    internal const string NoProvider = "No AI Source is connected; narration needs your own audio and visuals become text cards.";

    /// <summary>What this worker can produce now.</summary>
    internal async Task<OperatorReadiness> CurrentAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        if (media.Provider == "None")
        {
            notes.Add(NoProvider);
        }
        else if (!media.Speech && !media.Images)
        {
            notes.Add($"{media.Provider} has no speech or image service in Scout; narration needs your own audio and visuals become text cards.");
        }
        else if (!media.Speech)
        {
            notes.Add($"{media.Provider} has no speech service in Scout; narration needs your own audio.");
        }
        else if (!media.Images)
        {
            notes.Add($"{media.Provider} has no image service in Scout; visuals become text cards unless you attach your own images.");
        }

        var tokenFile = youtube.Value.TokenFile;
        var signedIn = !string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile);
        if (!signedIn)
        {
            notes.Add(NotSignedIn);
        }

        var assembly = await ffmpeg.ProbeAsync(ct).ConfigureAwait(false);
        notes.Add(assembly.Note);
        if (schedule?.Missed is { } missed)
        {
            notes.Add(missed);
        }

        return new OperatorReadiness(media.Speech, media.Images, media.Provider, assembly.Tools is not null, signedIn, notes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failureLogged = false;
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                try
                {
                    await controlPlane.ReportReadinessAsync(await CurrentAsync(stoppingToken).ConfigureAwait(false), stoppingToken).ConfigureAwait(false);
                    failureLogged = false;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    if (!failureLogged)
                    {
                        logger.LogWarning(
                            "The worker could not report its readiness: {Reason}",
                            ex is InvalidOperationException ? ex.Message : ex.GetType().Name);
                        failureLogged = true;
                    }
                }

            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
