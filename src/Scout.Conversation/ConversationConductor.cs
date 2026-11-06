using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Memory;
using Scout.Conversation.Streaming;
using Scout.Conversation.Telemetry;

namespace Scout.Conversation;

/// <summary>
/// Adapter-facing handler: routes an inbound message to a streaming response
/// and pumps it into the channel-bound sink. Wraps the work in an OTel activity.
/// A sender outside <see cref="AccessOptions"/> gets a fixed refusal reply and
/// nothing else runs. A message of exactly <c>pair &lt;code&gt;</c> (any case),
/// or of exactly six digits in a direct message while a code is pending, is
/// checked against the one-time pairing code before the allow-list; only a
/// direct message with the valid, unexpired code succeeds. The code is burned
/// when it is sent outside a direct message, bare or as <c>pair &lt;code&gt;</c>,
/// or after five wrong codes. The local owner skips both checks. A voice message from an
/// accepted sender is downloaded, transcribed on this machine and routed as
/// text, and the reply opens with a quote of what was heard.
/// Failures surface through the sink's <see cref="IResponseSink.FailAsync"/> and
/// never crash the host.
/// </summary>
public sealed partial class ConversationConductor(
    IConversationRouter router,
    StreamPump pump,
    ILogger<ConversationConductor>? logger,
    AccessPolicy? access,
    IAudioTranscriber? transcriber = null,
    IPairingSignal? pairingSignal = null,
    TimeProvider? timeProvider = null,
    IConversationKeyProvider? keyProvider = null)
{
    private const string NotAllowedReply = "Scout only answers its configured users.";
    private const string NoAllowedUsersReply =
        "Scout is not set up for anyone yet. If you are the owner, open the Scout desktop app, go to Settings, "
        + "choose Pair from chat and send the 6-digit code here in a direct message.";
    private const string PairedReply = "Paired. Scout will answer you from now on.";
    private const string PairingRefusedReply =
        "That pairing code is not valid. Check the code in Scout's Settings and send it again.";
    private const int MaxFailedPairingAttempts = 5;
    internal const string NoTranscriberReply =
        "Voice messages need a Whisper model. Download one in the Scout app under AI Sources, Local Models.";
    internal const string AudioTooLargeReply = "That voice message is too large. Scout transcribes up to 25 MB.";
    internal const string AudioTooLongReply = "That voice message is too long. Scout transcribes up to 5 minutes.";
    internal const string AudioDownloadFailedReply = "Scout could not download that voice message. Try sending it again.";
    internal const string TranscriptionFailedReply = "Scout could not transcribe that voice message. Try again or type the question.";
    internal const string NothingHeardReply = "Scout heard no speech in that voice message.";
    internal const string TranscriberBusyReply = "Scout is transcribing a recording right now; try the voice message again in a few minutes.";
    internal const long MaxAudioBytes = 25L * 1024 * 1024;
    internal static readonly TimeSpan MaxAudioDuration = TimeSpan.FromMinutes(5);
    private const int QuotedTranscriptLength = 200;
    private static readonly TimeSpan DefaultAudioDownloadTimeout = TimeSpan.FromSeconds(30);

    private readonly IConversationRouter _router = router ?? throw new ArgumentNullException(nameof(router));
    private readonly StreamPump _pump = pump ?? throw new ArgumentNullException(nameof(pump));
    private readonly ILogger<ConversationConductor> _logger = logger ?? NullLogger<ConversationConductor>.Instance;
    private readonly AccessPolicy _access = access ?? new AccessPolicy(new AccessOptions());
    private readonly IAudioTranscriber? _transcriber = transcriber;
    private readonly IPairingSignal? _pairingSignal = pairingSignal;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IConversationKeyProvider _keyProvider = keyProvider ?? new ConversationKeyProvider();
    private int _pairingUsed;
    private int _failedPairingAttempts;

    /// <summary>Ceiling on downloading one voice message, body included.</summary>
    internal TimeSpan AudioDownloadTimeout { get; init; } = DefaultAudioDownloadTimeout;

    /// <summary>
    /// Builds the conductor from bound <see cref="AccessOptions"/>, without voice
    /// transcription: the allow-list and the pairing code both come from them.
    /// </summary>
    public ConversationConductor(
        IConversationRouter router,
        StreamPump pump,
        ILogger<ConversationConductor>? logger = null,
        IOptions<AccessOptions>? access = null,
        IPairingSignal? pairingSignal = null,
        TimeProvider? timeProvider = null)
        : this(router, pump, logger, new AccessPolicy(access?.Value ?? new AccessOptions()), null, pairingSignal, timeProvider)
    {
    }

    [GeneratedRegex(@"^(?<prefix>pair +)?(?<code>[0-9]{6})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PairingPattern();

    /// <summary>
    /// Handle one inbound message. Matches the adapter handler signature
    /// <c>Func&lt;ChatMessage, IResponseSink, CancellationToken, Task&gt;</c>.
    /// </summary>
    public async Task HandleAsync(
        ChatMessage message,
        IResponseSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(sink);

        using var activity = ScoutActivitySource.Instance.StartActivity("conversation.handle");
        activity?.SetTag("scout.channel_id", message.ChannelId);
        using var origin = ChatOrigin.Enter(
            message.OriginReference, message.IsLocalOwner || _access.IsOwner(message.UserId), _time, _keyProvider.DeriveKey(message));
        try
        {
            if (!message.IsLocalOwner
                && PairingPattern().Match(message.Text.Trim()) is { Success: true } attempt
                && (attempt.Groups["prefix"].Success
                    || (PairingPending() && (message.IsDirectMessage || IsPendingCode(attempt.Groups["code"].Value)))))
            {
                var paired = TryPair(message, attempt.Groups["code"].Value);
                _logger.LogInformation(
                    "Pairing attempt for channel {ChannelId}: {Outcome}.",
                    message.ChannelId, paired ? "accepted" : "refused");
                await sink.BeginAsync(cancellationToken).ConfigureAwait(false);
                await sink.CompleteAsync(paired ? PairedReply : PairingRefusedReply, [], cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!message.IsLocalOwner && RefusalFor(message.UserId) is { } refusal)
            {
                _logger.LogInformation(
                    "Refused an inbound message for channel {ChannelId}: the sender is not an allowed user.",
                    message.ChannelId);
                await sink.BeginAsync(cancellationToken).ConfigureAwait(false);
                await sink.CompleteAsync(refusal, [], cancellationToken).ConfigureAwait(false);
                return;
            }

            if (message.Audio is { } audio)
            {
                await sink.BeginAsync(cancellationToken).ConfigureAwait(false);
                var (transcript, problem) = await TranscribeAsync(message.ChannelId, audio, cancellationToken)
                    .ConfigureAwait(false);
                if (problem is not null)
                {
                    await sink.CompleteAsync(problem, [], cancellationToken).ConfigureAwait(false);
                    return;
                }

                message = new ChatMessage(message.UserId, message.ChannelId, transcript!, message.Timestamp, IsDirectMessage: message.IsDirectMessage)
                { IsLocalOwner = message.IsLocalOwner, OriginReference = message.OriginReference, Image = message.Image };
                sink = new VoiceReplySink(sink, Quote(transcript!));
            }

            _logger.LogInformation(
                "Routing inbound message for channel {ChannelId} (textLen={Length}).",
                message.ChannelId, message.Text.Length);
            var response = _router.Route(message);
            await _pump.RunAsync(response, sink, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Response completed for channel {ChannelId}.", message.ChannelId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // Routing or sink lifecycle failed before/around the pump. Surface
            // through the sink rather than crashing the host.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Conversation handling failed for channel {ChannelId}.",
                message.ChannelId);
            try
            {
                await sink.FailAsync(string.Empty, ex.Message, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception sinkEx)
            {
                _logger.LogWarning(sinkEx, "Sink FailAsync also failed; swallowing.");
            }
        }
    }

    private bool PairingPending() =>
        _pairingSignal is not null
        && !string.IsNullOrWhiteSpace(_access.PairingCode)
        && _access.PairingExpiresAt is { } expiresAt
        && _time.GetUtcNow() < expiresAt
        && Volatile.Read(ref _pairingUsed) == 0;

    private bool IsPendingCode(string code) =>
        string.Equals(_access.PairingCode?.Trim(), code, StringComparison.Ordinal);

    private bool TryPair(ChatMessage message, string code)
    {
        if (!PairingPending())
        {
            return false;
        }

        var matches = IsPendingCode(code);
        if (matches && message.IsDirectMessage)
        {
            if (Interlocked.Exchange(ref _pairingUsed, 1) == 1)
            {
                return false;
            }

            _pairingSignal!.Paired(message.UserId);
            return true;
        }

        if (matches || Interlocked.Increment(ref _failedPairingAttempts) >= MaxFailedPairingAttempts)
        {
            BurnPairingCode();
        }

        return false;
    }

    private void BurnPairingCode()
    {
        if (Interlocked.Exchange(ref _pairingUsed, 1) == 1)
        {
            return;
        }

        try
        {
            _pairingSignal!.Burned();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell the desktop app that the pairing code was burned.");
        }
    }

    private async Task<(string? Transcript, string? Problem)> TranscribeAsync(
        string channelId,
        AudioAttachment audio,
        CancellationToken cancellationToken)
    {
        if (_transcriber is null)
        {
            return (null, NoTranscriberReply);
        }
        if (audio.SizeBytes > MaxAudioBytes)
        {
            return (null, AudioTooLargeReply);
        }
        if (audio.Duration > MaxAudioDuration)
        {
            return (null, AudioTooLongReply);
        }

        MemoryStream? bytes;
        try
        {
            using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            download.CancelAfter(AudioDownloadTimeout);
            bytes = await DownloadAsync(audio, download.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not download a voice message for channel {ChannelId}.", channelId);
            return (null, AudioDownloadFailedReply);
        }
        if (bytes is null)
        {
            return (null, AudioTooLargeReply);
        }

        string transcript;
        var started = Stopwatch.GetTimestamp();
        using (bytes)
        {
            try
            {
                transcript = await _transcriber.TranscribeAsync(bytes, audio.ContentType, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TranscriptionBusyException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("A voice message for channel {ChannelId} waited too long for the speech model.", channelId);
                return (null, TranscriberBusyReply);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not transcribe a voice message for channel {ChannelId}.", channelId);
                return (null, TranscriptionFailedReply);
            }
        }

        _logger.LogInformation(
            "Transcribed a voice message for channel {ChannelId} (transcriptLen={Length}, durationMs={DurationMs}).",
            channelId, transcript.Length, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return string.IsNullOrWhiteSpace(transcript) ? (null, NothingHeardReply) : (transcript.Trim(), null);
    }

    private static async Task<MemoryStream?> DownloadAsync(AudioAttachment audio, CancellationToken cancellationToken)
    {
        var source = await audio.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var bytes = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                bytes.Write(chunk, 0, read);
                if (bytes.Length > MaxAudioBytes)
                {
                    await bytes.DisposeAsync().ConfigureAwait(false);
                    return null;
                }
            }
            bytes.Position = 0;
            return bytes;
        }
    }

    private static string Quote(string transcript)
    {
        var heard = transcript.ReplaceLineEndings(" ");
        if (heard.Length > QuotedTranscriptLength)
        {
            heard = heard[..QuotedTranscriptLength];
        }
        return "> (voice) " + heard + "\n\n";
    }

    private string? RefusalFor(string userId)
    {
        if (_access.IsAllowed(userId))
        {
            return null;
        }
        return _access.HasAllowList ? NotAllowedReply : NoAllowedUsersReply;
    }
}
