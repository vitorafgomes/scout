using System.Runtime.CompilerServices;
using System.Text;
using Scout.Conversation.Abstractions;
using Whisper.net;

namespace Scout.Transcription.Whisper;

/// <summary>
/// <see cref="IAudioTranscriber"/> that runs a local Whisper (ggml) model through
/// Whisper.net, over Ogg Opus voice messages or 16 kHz mono WAV recordings. Audio never leaves the machine. The model is loaded on the first
/// voice message, not at startup, and unloaded after <see cref="IdleUnload"/>
/// without one; the spoken language is detected unless a decode names it, and
/// Whisper uses half of the processors so a local LLM keeps running alongside. One transcription runs at a time; a voice message waits at most
/// <see cref="VoiceWait"/> for the model and is then refused with <see cref="TranscriptionBusyException"/>, while a
/// recording waits for as long as it takes.
/// </summary>
public sealed class WhisperAudioTranscriber : IAudioTranscriber, IDisposable
{
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan VoiceWait = TimeSpan.FromSeconds(30);
    private static readonly int Threads = Math.Max(1, Environment.ProcessorCount / 2);

    private readonly string _modelPath;
    private readonly Func<string, LoadedModel> _load;
    private readonly TimeProvider _time;
    private readonly ITimer _idleTimer;

    // ponytail: one transcription at a time, because a loaded large model holds GBs and each Whisper state hundreds of MB; a pool sized to memory if voice traffic queues.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LoadedModel? _model;
    private DateTimeOffset _lastUsed;

    /// <summary>Creates a transcriber over the ggml model at <paramref name="modelPath"/>.</summary>
    public WhisperAudioTranscriber(string modelPath)
        : this(modelPath, LoadWhisper, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="load"/> stands in for loading the ggml model and
    /// <paramref name="time"/> drives the idle unload.
    /// </summary>
    internal WhisperAudioTranscriber(string modelPath, Func<string, LoadedModel> load, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        _modelPath = modelPath;
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _idleTimer = _time.CreateTimer(_ => UnloadIfIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(Stream audio, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var samples = IsWav(contentType)
            ? WavDecoder.DecodeMono16k(audio, MaxDuration)
            : IsOgg(contentType)
                ? OggOpusDecoder.DecodeMono16k(audio, MaxDuration)
                : throw new NotSupportedException(
                    $"Voice messages in {contentType} are not supported; only Ogg Opus and WAV are.");

        var text = new StringBuilder();
        await foreach (var (segment, _) in RunAsync(samples, Decoding.VoiceMessage, VoiceWait, cancellationToken).ConfigureAwait(false))
        {
            text.Append(segment.Text);
        }

        return text.ToString().Trim();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TranscribedSegment> TranscribeSegmentsAsync(
        Stream wav, TimeSpan maxDuration, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wav);
        var samples = WavDecoder.DecodeMono16k(wav, maxDuration);
        await foreach (var (segment, _) in RunAsync(samples, Decoding.VoiceMessage, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false))
        {
            yield return segment;
        }
    }

    /// <summary>
    /// Decodes <paramref name="samples"/> once the model is free and hands the
    /// trimmed text and the language it was decoded in to <paramref name="onText"/>
    /// before releasing the model.
    /// </summary>
    internal async Task DecodeAsync(
        float[] samples, Decoding decoding, Func<string, string?, Task> onText, CancellationToken cancellationToken)
    {
        await EnterAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        await DecodeHeldAsync(samples, decoding, onText, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes <paramref name="samples"/> and decodes them in the background only when
    /// the model is free right now; returns null, taking and decoding nothing, when it is busy.
    /// </summary>
    internal Task? TryDecode(
        Func<float[]> samples, Decoding decoding, Func<string, string?, Task> onText, CancellationToken cancellationToken)
    {
#pragma warning disable CA2016 // TryDecode never throws: a cancelled session drops the frame instead of raising from a sync path
        if (!_gate.Wait(0))
#pragma warning restore CA2016
        {
            return null;
        }
        var taken = samples();
        return Task.Run(() => DecodeHeldAsync(taken, decoding, onText, cancellationToken), CancellationToken.None);
    }

    private async Task DecodeHeldAsync(
        float[] samples, Decoding decoding, Func<string, string?, Task> onText, CancellationToken cancellationToken)
    {
        try
        {
            _model ??= _load(_modelPath);
            var text = new StringBuilder();
            string? language = null;
            await foreach (var (segment, heardIn) in _model.Segments(samples, decoding, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text);
                language ??= heardIn;
            }
            await onText(text.ToString().Trim(), language).ConfigureAwait(false);
        }
        finally
        {
            _lastUsed = _time.GetUtcNow();
            _idleTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private async Task EnterAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (wait == Timeout.InfiniteTimeSpan)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var bound = new CancellationTokenSource(wait, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, bound.Token);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (bound.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionBusyException($"Another transcription held the speech model for more than {wait.TotalSeconds:0} seconds.", ex);
        }
    }

    private async IAsyncEnumerable<(TranscribedSegment Segment, string? Language)> RunAsync(
        float[] samples, Decoding decoding, TimeSpan wait, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnterAsync(wait, cancellationToken).ConfigureAwait(false);
        try
        {
            _model ??= _load(_modelPath);
            await foreach (var heard in _model.Segments(samples, decoding, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return heard;
            }
        }
        finally
        {
            _lastUsed = _time.GetUtcNow();
            _idleTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <summary>Releases the loaded model.</summary>
    public void Dispose()
    {
        _idleTimer.Dispose();
        _model?.Handle.Dispose();
        _model = null;
    }

    // A transcription in progress holds the gate and re-arms the timer when it ends.
    private void UnloadIfIdle()
    {
        if (!_gate.Wait(0))
        {
            return;
        }
        try
        {
            if (_model is not null && _time.GetUtcNow() - _lastUsed >= IdleUnload)
            {
                _model.Handle.Dispose();
                _model = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static LoadedModel LoadWhisper(string modelPath)
    {
        var factory = WhisperFactory.FromPath(modelPath);
        return new LoadedModel(Segments, factory);

        async IAsyncEnumerable<(TranscribedSegment Segment, string? Language)> Segments(
            float[] samples, Decoding decoding, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var builder = factory.CreateBuilder().WithThreads(Threads);
            if (decoding.Language is { } language)
            {
                builder.WithLanguage(language);
            }
            else
            {
                builder.WithLanguageDetection();
            }
            if (decoding.Greedy)
            {
                builder.WithGreedySamplingStrategy();
            }
            if (decoding.BeamSize is { } beamSize)
            {
                builder.WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(beamSize));
            }
            if (decoding.NoContext)
            {
                builder.WithNoContext();
            }
            if (decoding.SingleSegment)
            {
                builder.WithSingleSegment();
            }
            if (decoding.AudioContextSize is { } audioContext)
            {
                builder.WithAudioContextSize(audioContext);
            }
            var processor = builder.Build();
            await using (processor.ConfigureAwait(false))
            {
                await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
                {
                    yield return (new TranscribedSegment(segment.Start, segment.End, segment.Text), segment.Language);
                }
            }
        }
    }

    private static bool IsWav(string contentType)
        => contentType.StartsWith("audio/wav", StringComparison.OrdinalIgnoreCase)
           || contentType.StartsWith("audio/x-wav", StringComparison.OrdinalIgnoreCase);

    private static bool IsOgg(string contentType)
        => contentType.StartsWith("audio/ogg", StringComparison.OrdinalIgnoreCase)
           || contentType.StartsWith("audio/opus", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A loaded model: the segments it hears in 16 kHz mono samples under a decoding, each with the language it was
    /// heard in, and how to release it.
    /// </summary>
    internal sealed record LoadedModel(
        Func<float[], Decoding, CancellationToken, IAsyncEnumerable<(TranscribedSegment Segment, string? Language)>> Segments,
        IDisposable Handle);

    /// <summary>
    /// How one decode runs: greedy or a beam of <see cref="BeamSize"/>, with or
    /// without the previous text as context, as one segment, over an audio
    /// context shorter than Whisper's 30 s when <see cref="AudioContextSize"/> is
    /// set, and in <see cref="Language"/> when set instead of detecting it.
    /// </summary>
    internal sealed record Decoding(
        bool Greedy, int? BeamSize, bool NoContext, bool SingleSegment, int? AudioContextSize, string? Language = null)
    {
        /// <summary>A voice message or a recording: Whisper's defaults.</summary>
        internal static readonly Decoding VoiceMessage = new(false, null, false, false, null);

        /// <summary>A live window: fast and rough, over at most 20 s of audio.</summary>
        internal static readonly Decoding LivePartial = new(true, null, true, true, 1000);

        /// <summary>
        /// A live window of <paramref name="samples"/> in <paramref name="language"/>,
        /// or detected when null, over an audio context that covers the window and 2 s
        /// more, from 384 to 1000.
        /// </summary>
        internal static Decoding LivePartialOver(int samples, string? language)
            => LivePartial with { AudioContextSize = Math.Clamp(samples / 320 + 100, 384, 1000), Language = language };

        /// <summary>An ended utterance: beam search over the whole utterance.</summary>
        internal static readonly Decoding LiveFinal = new(false, 5, false, false, null);
    }
}
