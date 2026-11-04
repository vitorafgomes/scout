using Scout.Conversation.Abstractions;

namespace Scout.Operator;

/// <summary>
/// Turns the owner's recording into the project's script, on this machine: the recording must be in the media directory,
/// carry a picture and a sound and last at most <see cref="MaxRecordingMs"/>; its sound is taken out with ffmpeg as 16 kHz
/// mono WAV into <see cref="WavFile"/> in the same directory, removed when the stage ends, and transcribed by the local
/// speech-to-text model, the whole of it within <see cref="LimitFor"/>. The script has no hook and no call to action and
/// one section per segment heard, with no heading and the segment's start and end; the script so far is saved at most
/// once every <see cref="ProgressEvery"/>, silence included, the first time as soon as the recording's length is known.
/// However the stage ends, the transcription still running is cancelled before its segments are released, and a
/// transcriber that fails names the failure's type and the first line of its message.
/// </summary>
internal sealed class TranscriptionStage(FfmpegToolset ffmpeg, MediaProbe probe, TimeProvider time, IAudioTranscriber? transcriber = null)
{
    internal const string WavFile = "recording.wav";

    internal const int MaxRecordingMs = 60 * 60 * 1000;

    internal static readonly TimeSpan ProgressEvery = TimeSpan.FromMinutes(1);

    internal const string NoRecording = "The project has no recording; attach it in Scout Desktop, Production.";

    internal const string NoPicture = "The recording has no picture; attach a video file.";

    internal const string NoSound = "The recording has no sound to transcribe.";

    internal const string TooLong = "The recording is longer than 60 minutes; Scout takes at most 60.";

    internal const string NoModel = "Scout has no speech-to-text model on this machine; download one in AI Sources, Local Models.";

    internal const string ExtractFailed = "Scout could not take the sound out of the recording";

    internal const string CouldNotTranscribe = "Scout could not transcribe the recording";

    internal const int MaxCauseLength = 120;

    internal const string NoSpeech = "Scout heard no speech in the recording.";

    internal const string NotFound = "Scout could not find the recording in the project's media folder.";

    internal static string Stopped(TimeSpan limit) => $"Scout stopped transcribing the recording after {(int)Math.Round(limit.TotalMinutes, MidpointRounding.AwayFromZero)} minutes.";

    /// <summary>How long a transcription may take: ten minutes plus six times the recording's length.</summary>
    internal static TimeSpan LimitFor(int recordingMs) => TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(6L * recordingMs);

    /// <summary>
    /// The transcript of <paramref name="project"/>'s recording as its script; <paramref name="save"/> takes the recording's
    /// length and the script so far and answers false once the project is no longer transcribing, and the stage then stops
    /// and answers null.
    /// </summary>
    public async Task<OperatorScript?> TranscribeAsync(
        OperatorProjectState project, string directory, Func<int, OperatorScript, Task<bool>> save, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(save);
        var recording = project.RecordingFile ?? throw new InvalidOperationException(NoRecording);
        var model = transcriber ?? throw new InvalidOperationException(NoModel);
        var found = await ffmpeg.ProbeAsync(ct).ConfigureAwait(false);
        var tools = found.Tools ?? throw new InvalidOperationException(found.Note);
        if (!MediaFiles.Exists(directory, recording))
        {
            throw new InvalidOperationException(NotFound);
        }

        var streams = await probe.InspectAsync(tools, directory, recording, ct).ConfigureAwait(false);
        if (!streams.HasPicture)
        {
            throw new InvalidOperationException(NoPicture);
        }

        if (!streams.HasSound)
        {
            throw new InvalidOperationException(NoSound);
        }

        if (streams.DurationMs > MaxRecordingMs)
        {
            throw new InvalidOperationException(TooLong);
        }

        var sections = new List<OperatorScriptSection>();
        if (!await save(streams.DurationMs, Script(sections)).ConfigureAwait(false))
        {
            return null;
        }

        var savedAt = time.GetUtcNow();
        var limit = LimitFor(streams.DurationMs);
        try
        {
            var source = MediaFiles.PathFor(directory, recording);
            await MediaFiles.RenderAsync(
                directory,
                WavFile,
                ExtractFailed,
                temp => ffmpeg.RunAsync(tools, ExtractArguments(source, temp), streams.DurationMs, ExtractFailed, ct),
                ct).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(limit, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var wav = File.OpenRead(MediaFiles.PathFor(directory, WavFile));
            await using (wav.ConfigureAwait(false))
            {
                var segments = model.TranscribeSegmentsAsync(wav, TimeSpan.FromMilliseconds(MaxRecordingMs), linked.Token)
                    .GetAsyncEnumerator(linked.Token);
                await using (segments.ConfigureAwait(false))
                {
                    try
                    {
                        while (await NextAsync(segments, timeout, limit, ct).ConfigureAwait(false))
                        {
                            var heard = segments.Current;
                            var text = heard.Text.Trim();
                            if (text.Length > 0)
                            {
                                sections.Add(new OperatorScriptSection(string.Empty, text) { StartMs = Ms(heard.Start), EndMs = Ms(heard.End) });
                            }

                            if (time.GetUtcNow() - savedAt >= ProgressEvery)
                            {
                                if (!await save(streams.DurationMs, Script(sections)).ConfigureAwait(false))
                                {
                                    return null;
                                }

                                savedAt = time.GetUtcNow();
                            }
                        }
                    }
                    finally
                    {
                        await linked.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            MediaFiles.Delete(directory, WavFile);
        }

        return sections.Count == 0 ? throw new InvalidOperationException(NoSpeech) : Script(sections);
    }

    internal static List<string> ExtractArguments(string recording, string output)
        => ["-hide_banner", "-nostdin", "-y", "-i", recording, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", output];

    private static async Task<bool> NextAsync(
        IAsyncEnumerator<TranscribedSegment> segments, CancellationTokenSource timeout, TimeSpan limit, CancellationToken ct)
    {
        try
        {
            return await segments.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(Stopped(limit), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(CouldNotTranscribeBecause(ex), ex);
        }
    }

    internal static string CouldNotTranscribeBecause(Exception ex)
    {
        var line = ex.Message.Split('\n', 2)[0].Trim();
        return $"{CouldNotTranscribe} ({ex.GetType().Name}: {(line.Length > MaxCauseLength ? line[..MaxCauseLength].TrimEnd() : line)})";
    }

    private static OperatorScript Script(IReadOnlyList<OperatorScriptSection> sections)
        => new(string.Empty, [.. sections], string.Empty, [], string.Empty, [], []);

    private static int Ms(TimeSpan at) => (int)Math.Clamp(Math.Round(at.TotalMilliseconds), 0, int.MaxValue);
}
