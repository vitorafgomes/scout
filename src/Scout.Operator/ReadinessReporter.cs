using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Plugin.YouTube;

namespace Scout.Operator;

/// <summary>
/// Tells the control plane what this worker can produce, when it starts and every <see cref="Interval"/>: speech as the
/// connected provider's speech service or the bundled voices allow, the narration voices of both, each bundled one with its sample file when the voice samples folder holds it, images as the provider's
/// image service allows, whether pictures come from Openverse without one, the provider's name, whether videos can be assembled, with a note naming the encoder or why not, whether the
/// YouTube token file the desktop writes at sign-in is on disk, whether a speech-to-text model can transcribe the owner's
/// recording, whether an animated character's mouth can move with its narration, whether the mouth on a presenter picture
/// can be found, how many of the day's pictures are left, the built-in presenters, and a note for each thing the owner has to
/// supply, the missing rhubarb program or mouth shapes only when the desktop named either, the face models and the built-in
/// presenters likewise.
/// A control plane that cannot take the report is logged once until it takes one again.
/// </summary>
internal sealed class ReadinessReporter(
    IOperatorControlPlane controlPlane,
    MediaCapabilities media,
    IOptions<YouTubeOptions> youtube,
    TimeProvider time,
    ILogger<ReadinessReporter> logger,
    FfmpegToolset ffmpeg,
    PiperSpeech piper,
    ScheduleNotes? schedule = null,
    OpenversePictures? stock = null,
    IAudioTranscriber? transcriber = null,
    RhubarbLipSync? lipSync = null,
    IFaceMouth? faces = null,
    BuiltInPresenters? presenters = null,
    VisualGenerator? visuals = null) : BackgroundService
{
    internal const string NoFaceModels = "Face detection: the models were not found.";

    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    internal const string NotSignedIn = "Scout is not signed in to YouTube; research goes on without the channel's own uploads.";

    internal const string NoProvider = "No AI Source is connected; narration needs your own audio and visuals become text cards.";

    internal const string NoProviderWithBundledVoices = "No AI Source is connected; narration uses the bundled voices and visuals become text cards.";

    internal const string StockPicturesNote =
        "Pictures without an AI image service come from Openverse (CC0, public domain or CC BY, credited in the description).";

    internal const string BundledVoicesUnusable =
        "The bundled voices are missing or cannot run, so narration needs the connected AI Source or your own audio.";

    /// <summary>What this worker can produce now.</summary>
    internal async Task<OperatorReadiness> CurrentAsync(CancellationToken ct)
    {
        var bundled = await piper.ProbeAsync(ct).ConfigureAwait(false);
        var notes = new List<string>();
        if (media.Provider == "None")
        {
            notes.Add(bundled.Count > 0 ? NoProviderWithBundledVoices : NoProvider);
        }
        else if (!media.Speech && !media.Images)
        {
            notes.Add(bundled.Count > 0
                ? $"{media.Provider} has no speech or image service in Scout; narration uses the bundled voices and visuals become text cards."
                : $"{media.Provider} has no speech or image service in Scout; narration needs your own audio and visuals become text cards.");
        }
        else if (!media.Speech)
        {
            notes.Add(bundled.Count > 0
                ? $"{media.Provider} has no speech service in Scout; narration uses the bundled voices unless you attach your own audio."
                : $"{media.Provider} has no speech service in Scout; narration needs your own audio.");
        }
        else if (!media.Images)
        {
            notes.Add($"{media.Provider} has no image service in Scout; visuals become text cards unless you attach a background to the project in Scout Desktop, Production, or connect an AI Source with pictures.");
        }

        if (!media.Images && stock is not null)
        {
            notes.Add(StockPicturesNote);
        }

        if (piper.Configured && bundled.Count == 0)
        {
            notes.Add(BundledVoicesUnusable);
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

        var lips = lipSync is null ? null : await lipSync.ProbeAsync(ct).ConfigureAwait(false);
        if (lipSync is { Configured: true })
        {
            notes.AddRange(lips!.Notes);
        }

        var faceModels = faces is not null && await faces.AvailableAsync(ct).ConfigureAwait(false);
        var faceDetection = faceModels && assembly.Tools is not null;
        if (faces is { Configured: true } && !faceModels)
        {
            notes.Add(NoFaceModels);
        }

        var builtIns = presenters?.List() ?? [];
        if (presenters is { Configured: true } && builtIns.Count == 0)
        {
            notes.Add(BuiltInPresenters.NotFound);
        }

        var samples = bundled.Count > 0 ? await SamplesDirectoryAsync(ct).ConfigureAwait(false) : null;
        IReadOnlyList<OperatorVoice> voices =
        [
            .. media.Speech ? media.SpeechVoices.Select(v => new OperatorVoice($"{media.Provider}:{v}", $"{v} ({media.Provider})", media.Provider)) : [],
            .. bundled.Select(v => new OperatorVoice(v.Id, v.Label, PiperSpeech.Source, SampleOf(samples, v.Id))),
        ];
        return new OperatorReadiness(media.Speech || bundled.Count > 0, media.Images, media.Provider, assembly.Tools is not null, signedIn, notes)
        {
            Voices = voices,
            StockPictures = stock is not null,
            TranscriptionAvailable = transcriber is not null,
            LipSyncAvailable = lips?.Available == true,
            FaceDetectionAvailable = faceDetection,
            PicturesLeftToday = media.Images ? visuals?.PicturesLeft ?? 0 : 0,
            BuiltInPresenters = [.. builtIns.Select(p => new OperatorBuiltInPresenter(p.Id, p.Label))],
        };
    }

    private bool _failureLogged;

    /// <summary>Report the current readiness at once instead of waiting for the next tick.</summary>
    internal async Task ReportNowAsync(CancellationToken ct)
    {
        try
        {
            await controlPlane.ReportReadinessAsync(await CurrentAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            _failureLogged = false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!_failureLogged)
            {
                logger.LogWarning(
                    "The worker could not report its readiness: {Reason}",
                    ex is InvalidOperationException ? ex.Message : ex.GetType().Name);
                _failureLogged = true;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                await ReportNowAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<string?> SamplesDirectoryAsync(CancellationToken ct)
    {
        try
        {
            return await controlPlane.GetVoiceSamplesDirectoryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string? SampleOf(string? samples, string voiceId)
        => samples is not null && MediaFiles.Exists(samples, VoiceSamples.FileFor(voiceId)) ? VoiceSamples.FileFor(voiceId) : null;
}
