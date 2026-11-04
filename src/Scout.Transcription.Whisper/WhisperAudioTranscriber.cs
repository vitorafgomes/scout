using System.Runtime.CompilerServices;
using System.Text;
using Scout.Conversation.Abstractions;
using Whisper.net;

namespace Scout.Transcription.Whisper;

/// <summary>
/// <see cref="IAudioTranscriber"/> that runs a local Whisper (ggml) model through
/// Whisper.net, over Ogg Opus voice messages or 16 kHz mono WAV recordings. Audio never leaves the machine. The model is loaded on the first
/// voice message, not at startup, and unloaded after <see cref="IdleUnload"/>
/// without one; the spoken language is detected, and Whisper uses half of the
/// processors so a local LLM keeps running alongside. One transcription runs at a time; a voice message waits at most
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
        await foreach (var segment in RunAsync(samples, VoiceWait, cancellationToken).ConfigureAwait(false))
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
        await foreach (var segment in RunAsync(samples, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false))
        {
            yield return segment;
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

    private async IAsyncEnumerable<TranscribedSegment> RunAsync(
        float[] samples, TimeSpan wait, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnterAsync(wait, cancellationToken).ConfigureAwait(false);
        try
        {
            _model ??= _load(_modelPath);
            await foreach (var segment in _model.Segments(samples, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return segment;
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

        async IAsyncEnumerable<TranscribedSegment> Segments(float[] samples, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var processor = factory.CreateBuilder().WithLanguageDetection().WithThreads(Threads).Build();
            await using (processor.ConfigureAwait(false))
            {
                await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
                {
                    yield return new TranscribedSegment(segment.Start, segment.End, segment.Text);
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

    /// <summary>A loaded model: the segments it hears in 16 kHz mono samples, and how to release it.</summary>
    internal sealed record LoadedModel(Func<float[], CancellationToken, IAsyncEnumerable<TranscribedSegment>> Segments, IDisposable Handle);
}
