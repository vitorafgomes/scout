using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scout.Operator;

/// <summary>
/// Makes one short sample of every bundled voice so the owner can hear a voice before choosing it: when the worker starts
/// and every <see cref="Interval"/>, for each voice <see cref="PiperSpeech"/> lists whose sample is not on disk yet, one at a
/// time, <see cref="Sentence"/> in that voice, written through <see cref="MediaFiles"/> as <see cref="FileFor"/> into the
/// folder the control plane gives and nowhere else. A voice that fails is logged once and tried again on the next round; a
/// control plane that cannot give the folder is logged once until it gives it again.
/// </summary>
internal sealed class VoiceSamples(
    IOperatorControlPlane controlPlane,
    PiperSpeech piper,
    ReadinessReporter readiness,
    TimeProvider time,
    ILogger<VoiceSamples> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    internal const string Sentence = "Hello. This is how I sound. Scout can narrate your videos with this voice.";

    private readonly HashSet<string> _failuresLogged = new(StringComparer.Ordinal);
    private bool _directoryFailureLogged;

    /// <summary>The sample's file name of <paramref name="voiceId"/>: its <c>:</c> and <c>#</c> turned to <c>-</c>, then <c>.wav</c>.</summary>
    internal static string FileFor(string voiceId) => voiceId.Replace(':', '-').Replace('#', '-') + ".wav";

    /// <summary>Make the sample of every listed voice that has none on disk yet; nothing is asked of the control plane without voices.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var voices = await piper.ProbeAsync(ct).ConfigureAwait(false);
        if (voices.Count == 0)
        {
            return;
        }

        string directory;
        try
        {
            directory = await controlPlane.GetVoiceSamplesDirectoryAsync(ct).ConfigureAwait(false);
            _directoryFailureLogged = false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!_directoryFailureLogged)
            {
                logger.LogWarning("The worker could not get the voice samples folder: {Reason}", Reason(ex));
                _directoryFailureLogged = true;
            }

            return;
        }

        var made = false;
        foreach (var voice in voices)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var file = FileFor(voice.Id);
            if (MediaFiles.Exists(directory, file))
            {
                continue;
            }

            try
            {
                await piper.SynthesizeAsync(voice.Id, Sentence, 1, directory, file, ct).ConfigureAwait(false);
                made = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (_failuresLogged.Add(voice.Id))
                {
                    logger.LogWarning("The sample of the voice {Voice} could not be made: {Reason}", voice.Id, Reason(ex));
                }
            }
        }

        if (made)
        {
            await readiness.ReportNowAsync(ct).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static string Reason(Exception ex) => ex is InvalidOperationException ? ex.Message : ex.GetType().Name;
}
