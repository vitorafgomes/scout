using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation.Abstractions;

namespace Scout.Transcription.Whisper;

/// <summary>
/// <see cref="ILiveTranscriber"/> over the same loaded Whisper model, decode gate
/// and idle unload as <see cref="WhisperAudioTranscriber"/>. Each window of an
/// utterance is decoded greedily for a partial text, and is dropped when the model
/// is busy; each ended utterance is decoded again with a beam of 5 for its final
/// text, and is never dropped. Partials after the session's first final are
/// decoded in that final's language instead of detecting it. Each ended utterance
/// is logged at Information with why it ended and how long it was, never its text,
/// and a session that ends without one logs how loud its loudest frame and its room were.
/// </summary>
internal sealed class WhisperLiveTranscriber(WhisperAudioTranscriber model, ILogger? logger = null) : ILiveTranscriber
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    internal WhisperAudioTranscriber Model => model;

    /// <inheritdoc />
    public ILiveSession Start(Func<LiveTranscript, Task> onEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        return new Session(model, _logger, onEvent, cancellationToken);
    }

    private sealed class Session(
        WhisperAudioTranscriber model,
        ILogger logger,
        Func<LiveTranscript, Task> onEvent,
        CancellationToken lifetime) : ILiveSession
    {
        private readonly LiveUtteranceSegmenter _segmenter = new();
        private Action<LiveSegment>? _ready;
        private int _turns;
        private Task _partial = Task.CompletedTask;
        private Task _finals = Task.CompletedTask;
        private Exception? _failure;
        private string? _language;

        public ValueTask FeedAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken)
        {
            ThrowIfFailed();
            _segmenter.Feed(pcm16.Span, _ready ??= OnSegment);
            return ValueTask.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _segmenter.Flush(_ready ??= OnSegment);
            if (_turns == 0)
            {
                logger.LogInformation(
                    "Live session ended with no speech heard (loudest frame RMS {Rms:F3}, room {Room:F3})",
                    _segmenter.LoudestRms,
                    _segmenter.Room);
            }
            await Task.WhenAll(_partial, _finals).WaitAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfFailed();
        }

        private void OnSegment(LiveSegment segment)
        {
            if (segment.Kind == LiveSegmentKind.UtteranceEnded)
            {
                _turns++;
                logger.LogInformation(
                    "Live turn ended ({Reason}) after {Milliseconds} ms of audio; decoding its final text.",
                    segment.End,
                    segment.Length * 1000L / LiveUtteranceSegmenter.SampleRate);
                QueueFinal(segment.Samples());
            }
            else if (_finals.IsCompleted
                     && model.TryDecode(
                         segment.Samples,
                         WhisperAudioTranscriber.Decoding.LivePartialOver(segment.Length, Volatile.Read(ref _language)),
                         EmitPartial,
                         lifetime) is { } decode)
            {
                _partial = Observe(decode);
            }
        }

        private Task EmitPartial(string text, string? language)
            => text.Length == 0 ? Task.CompletedTask : onEvent(new LiveTranscript(text, false));

        private void QueueFinal(float[] samples)
        {
            var previous = _finals;
            _finals = Task.Run(
                async () =>
                {
                    await previous.ConfigureAwait(false);
                    await Observe(model.DecodeAsync(
                        samples,
                        WhisperAudioTranscriber.Decoding.LiveFinal,
                        (text, language) =>
                        {
                            if (language is not null)
                            {
                                Interlocked.CompareExchange(ref _language, language, null);
                            }
                            return onEvent(new LiveTranscript(text, true));
                        },
                        lifetime)).ConfigureAwait(false);
                },
                CancellationToken.None);
        }

        private async Task Observe(Task decode)
        {
            try
            {
                await decode.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure, ex, null);
            }
        }

        private void ThrowIfFailed()
        {
            if (Volatile.Read(ref _failure) is { } failure)
            {
                ExceptionDispatchInfo.Throw(failure);
            }
        }
    }
}
