using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;

namespace Scout.Conversation.Streaming;

/// <summary>
/// Transport-agnostic stream-drive loop. Owns the response lifecycle once:
/// opens the sink, accumulates tokens into a cumulative buffer, throttles
/// interim <see cref="IResponseSink.UpdateAsync"/> calls per the sink's
/// capabilities, then issues a single terminal Complete or Fail. The sink stays
/// a thin transport and owns its own transport resilience (rate limits,
/// retries). Cadence, length, and the overall timeout derive from
/// <see cref="StreamPumpOptions"/> tightened by <see cref="RenderCapabilities"/>,
/// so the pump is ignorant of which platform it drives.
/// </summary>
public sealed class StreamPump
{
    // Tail-preserving truncation marker (recent text is more useful than head).
    private const string TruncationMarker = "... (truncated)\n\n";
    private const string CutMarker = "... (truncated)";
    private const string SentenceSeparator = "\n\n";
    private static readonly int TruncationMarkerLength = TruncationMarker.Length;
    private static readonly TimeSpan FenceMatchTimeout = TimeSpan.FromSeconds(1);

    private readonly StreamPumpOptions _options;

    public StreamPump(StreamPumpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.EditInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "EditInterval must be non-negative.");
        }

        if (options.TerminalWriteGrace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "TerminalWriteGrace must be non-negative.");
        }

        _options = options;
    }

    /// <summary>
    /// Drive <paramref name="response"/> into <paramref name="sink"/>. Begins
    /// the sink, streams updates, and finishes with exactly one terminal call.
    /// Caller cancellation propagates as <see cref="OperationCanceledException"/>
    /// without forcing a terminal edit. A model rate limit
    /// (<see cref="LlmRateLimitException"/>) finalizes via
    /// <see cref="IResponseSink.CompleteAsync"/> with a friendly user-facing
    /// message, and so does a provider quota or billing refusal
    /// (<see cref="LlmQuotaExceededException"/>) or a sign-in the provider needs
    /// renewed (<see cref="LlmSignInRequiredException"/>) or a rejected key (<see cref="LlmKeyRejectedException"/>) or a model the provider does not have (<see cref="LlmModelNotFoundException"/>), or a picture the provider
    /// cannot read (<see cref="LlmVisionUnavailableException"/>), or an answer the model ended with no text
    /// (<see cref="LlmNoAnswerException"/>, whose message is the sentence); any other mid-stream failure is reported via
    /// <see cref="IResponseSink.FailAsync"/>. Neither escapes. Each of those terminal
    /// bodies fits the sink's length limit, the partial text giving way to the
    /// message, and each of those writes is bounded: it has what remains of the
    /// overall timeout, or <see cref="StreamPumpOptions.TerminalWriteGrace"/> once
    /// that timeout has fired, and the run stops waiting for it at that bound
    /// even when the sink ignores its token. A write the sink rejects or does
    /// not finish in time is logged once and the run returns.
    /// </summary>
    public async Task RunAsync(
        ChatResponse response,
        IResponseSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(sink);

        var caps = sink.Capabilities;
        var interval = EffectiveInterval(caps);
        var maxLength = EffectiveMaxLength(caps);

        // Interim editing is worthwhile only when the sink can either edit the
        // posted message repeatedly or show citations before the terminal call.
        // Otherwise the loop accumulates silently and emits a single Complete.
        var wantsInterim = caps.FineGrainedEdit || caps.SupportsInterimCitations;

        // The timeout bounds the whole run, including BeginAsync and the success
        // CompleteAsync, so non-edit platforms whose only write is the terminal
        // call are still guarded.
        using var linked = LinkTimeout(caps, cancellationToken, out var pumpToken);

        Task SayAsync(string partial, string sentence) => WriteTerminalAsync(
            token => sink.CompleteAsync(WithSentence(partial, sentence, maxLength), response.Citations, token),
            pumpToken,
            cancellationToken);

        await sink.BeginAsync(pumpToken).ConfigureAwait(false);

        var buffer = new StringBuilder();
        DateTimeOffset? lastEditAt = null;

        try
        {
            await foreach (var token in response.Tokens
                .WithCancellation(pumpToken)
                .ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(token))
                {
                    buffer.Append(token);
                }

                if (!wantsInterim)
                {
                    continue;
                }

                if (!ShouldEditNow(lastEditAt, interval))
                {
                    continue;
                }

                var body = RenderInterim(buffer.ToString(), maxLength);
                await TryInterimUpdateAsync(sink, body, pumpToken).ConfigureAwait(false);
                lastEditAt = _options.TimeProvider.GetUtcNow();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled: surface it, do not force a terminal edit.
            throw;
        }
        catch (LlmRateLimitException)
        {
            // Transient, expected 429: finalize via Complete with a user-facing
            // message (FailAsync's reason is never rendered). Log quietly without
            // the stack — this is not an error.
            _options.Logger?.LogInformation(
                "StreamPump stopped on model rate limit (429); finalizing with a friendly message.");
            await SayAsync(buffer.ToString(), _options.RateLimitMessage).ConfigureAwait(false);
            return;
        }
        catch (LlmQuotaExceededException ex)
        {
            _options.Logger?.LogWarning(
                "StreamPump stopped: the AI provider refused the request for quota or billing (code {ProviderCode}); finalizing with a user-facing message.",
                ex.Code);
            var sentence = ex.Code switch
            {
                "subscription_sharing_usage_limit_exceeded" => _options.PlanUsageLimitMessage,
                "credit_balance_too_low" => _options.CreditExhaustedMessage,
                _ => _options.QuotaExceededMessage,
            };
            await SayAsync(buffer.ToString(), sentence).ConfigureAwait(false);
            return;
        }
        catch (LlmSignInRequiredException)
        {
            _options.Logger?.LogWarning(
                "StreamPump stopped: the AI provider needs the user to sign in again; finalizing with a user-facing message.");
            await SayAsync(buffer.ToString(), _options.SignInRequiredMessage).ConfigureAwait(false);
            return;
        }
        catch (LlmKeyRejectedException)
        {
            _options.Logger?.LogWarning(
                "StreamPump stopped: the AI provider rejected its key; finalizing with a user-facing message.");
            await SayAsync(buffer.ToString(), _options.KeyRejectedMessage).ConfigureAwait(false);
            return;
        }
        catch (LlmNoAnswerException ex)
        {
            _options.Logger?.LogWarning(
                "StreamPump stopped: the model ended its answer with no text ({Reason}); finalizing with that sentence.",
                ex.Message);
            await SayAsync(buffer.ToString(), ex.Message).ConfigureAwait(false);
            return;
        }
        catch (LlmVisionUnavailableException)
        {
            _options.Logger?.LogInformation(
                "StreamPump stopped: the message has an image and the AI provider cannot read images; finalizing with a user-facing message.");
            await SayAsync(string.Empty, _options.VisionUnavailableMessage).ConfigureAwait(false);
            return;
        }
        catch (LlmModelNotFoundException ex)
        {
            _options.Logger?.LogWarning(
                "StreamPump stopped: the AI Source does not have the model {Model}; finalizing with a user-facing message.",
                ex.Model);
            await SayAsync(
                buffer.ToString(),
                string.Format(CultureInfo.InvariantCulture, _options.ModelNotFoundMessage, ex.Model)).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            // Mid-stream failure (including overall-timeout): finalize with the
            // partial buffer and an error reason. Must not escape into the host.
            var reason = ex is OperationCanceledException ? "stream timed out" : ex.Message;
            _options.Logger?.LogWarning(
                ex, "StreamPump stream failed mid-flight; finalizing with partial content.");
            await WriteTerminalAsync(
                token => sink.FailAsync(FirstPart(buffer.ToString(), maxLength), reason, token),
                pumpToken,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = buffer.ToString();
        var blocks = string.Empty;
        if (TurnAttachments.Take() is { Count: > 0 } attached)
        {
            text = WithoutFencesLike(text, attached).Trim();
            blocks = BlocksThatFit(attached, maxLength);
        }

        var prose = Truncate(text, maxLength == int.MaxValue ? maxLength : maxLength - blocks.Length);
        var finalText = (prose.Length, blocks.Length) switch
        {
            (0, 0) => _options.EmptyStreamPlaceholder,
            (0, _) => blocks.TrimStart('\n'),
            _ => prose + blocks,
        };

        await sink.CompleteAsync(finalText, response.Citations, pumpToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the terminal write of a run that ended on an error. The write has what remains of the overall timeout, or
    /// the terminal write grace when that timeout has already fired; the caller's cancellation still stops it, and a
    /// caller that has already cancelled gets its cancellation with no write. The wait ends at the bound whether or
    /// not the sink honours its token: a write still running then is left to finish on its own and a failure it ends
    /// in later is observed. A write the sink rejects or does not finish in time is logged once, without the body or
    /// the exception, and swallowed.
    /// </summary>
    private async Task WriteTerminalAsync(
        Func<CancellationToken, Task> write,
        CancellationToken pumpToken,
        CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        using var grace = pumpToken.IsCancellationRequested
            ? new CancellationTokenSource(_options.TerminalWriteGrace, _options.TimeProvider)
            : null;
        using var bounded = grace is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(caller, grace.Token);
        var token = bounded?.Token ?? pumpToken;
        Task? writing = null;
        try
        {
            writing = write(token);
            await writing.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _options.Logger?.LogWarning(
                "StreamPump could not deliver the terminal message ({Failure}); the reply ends without it.",
                ex is OperationCanceledException ? "timed out" : ex.GetType().Name);
        }
        finally
        {
            _ = writing?.ContinueWith(
                static abandoned => abandoned.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The first part of a failed reply's partial text within <paramref name="budget"/>, marked at its end. A sink
    /// that cuts the text again to make room for its own footer cuts from the same end, so its cut takes this mark
    /// with it and the reply shows one.
    /// </summary>
    private static string FirstPart(string body, int budget)
    {
        if (budget == int.MaxValue || body.Length <= budget)
        {
            return body;
        }

        return budget <= CutMarker.Length ? body[..budget] : body[..(budget - CutMarker.Length)] + CutMarker;
    }

    /// <summary>
    /// The terminal body of an error: the partial text, a blank line and the sentence, within
    /// <paramref name="maxLength"/>. The sentence is kept whole and the partial text is cut to the room left, or
    /// dropped when that room cannot hold a cut; only a limit smaller than the sentence cuts the sentence.
    /// </summary>
    private static string WithSentence(string partial, string sentence, int maxLength)
    {
        if (sentence.Length >= maxLength)
        {
            return sentence[..maxLength];
        }

        var room = maxLength - sentence.Length - SentenceSeparator.Length;
        if (partial.Length == 0 || (partial.Length > room && room <= TruncationMarkerLength))
        {
            return sentence;
        }

        return Truncate(partial, room) + SentenceSeparator + sentence;
    }

    /// <summary>
    /// The model's text without any fenced block that opens like an attached block, from its opening line to its
    /// closing fence or the end of the text, so the reply carries the attached block alone.
    /// </summary>
    private static string WithoutFencesLike(string text, IReadOnlyList<string> attached)
    {
        foreach (var opening in attached.Select(block => block.Split('\n')[0].TrimEnd()).Distinct(StringComparer.Ordinal))
        {
            text = Regex.Replace(
                text, Regex.Escape(opening) + @"[ \t]*\r?\n[\s\S]*?(?:\r?\n```|\z)", string.Empty, RegexOptions.None, FenceMatchTimeout);
        }

        return text;
    }

    /// <summary>
    /// The attached blocks, each after a blank line, in the order attached, keeping only the blocks that fit whole in
    /// <paramref name="maxLength"/>; the model's text is cut to what the kept blocks leave.
    /// </summary>
    private static string BlocksThatFit(IReadOnlyList<string> attached, int maxLength)
    {
        var kept = new StringBuilder();
        foreach (var block in attached)
        {
            if (maxLength == int.MaxValue || kept.Length + block.Length + 2 <= maxLength)
            {
                kept.Append("\n\n").Append(block);
            }
        }

        return kept.ToString();
    }

    private CompositeDisposable? LinkTimeout(
        RenderCapabilities caps,
        CancellationToken caller,
        out CancellationToken pumpToken)
    {
        var timeout = EffectiveTimeout(caps);
        if (timeout is not { } window)
        {
            pumpToken = caller;
            return null;
        }

        // Deterministic timeout via the injected clock: a TimeProvider-backed
        // CTS fires the timeout, linked with the caller token.
        var timeoutCts = new CancellationTokenSource(window, _options.TimeProvider);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(caller, timeoutCts.Token);
        pumpToken = linkedCts.Token;
        return new CompositeDisposable(linkedCts, timeoutCts);
    }

    // Effective timeout = min(options.OverallTimeout, caps.MaxResponseDuration),
    // mirroring how EditIntervalFloor tightens the interval. Either may be null.
    private TimeSpan? EffectiveTimeout(RenderCapabilities caps)
    {
        var optTimeout = _options.OverallTimeout;
        var capTimeout = caps.MaxResponseDuration;
        if (optTimeout is null)
        {
            return capTimeout;
        }
        if (capTimeout is null)
        {
            return optTimeout;
        }
        return optTimeout < capTimeout ? optTimeout : capTimeout;
    }

    private TimeSpan EffectiveInterval(RenderCapabilities caps)
        => caps.EditIntervalFloor is { } floor && floor > _options.EditInterval
            ? floor
            : _options.EditInterval;

    private int EffectiveMaxLength(RenderCapabilities caps)
    {
        var sinkMax = caps.MaxBodyLength > 0 ? caps.MaxBodyLength : int.MaxValue;
        var optMax = _options.MaxBodyLength is { } m && m > 0 ? m : int.MaxValue;
        return Math.Min(sinkMax, optMax);
    }

    private bool ShouldEditNow(DateTimeOffset? lastEditAt, TimeSpan interval)
    {
        if (lastEditAt is null)
        {
            return true;
        }
        return _options.TimeProvider.GetUtcNow() - lastEditAt.Value >= interval;
    }

    private string RenderInterim(string snapshot, int maxLength)
    {
        var cursor = _options.Cursor;
        var budget = maxLength == int.MaxValue ? int.MaxValue : maxLength - cursor.Length;
        return Truncate(snapshot, budget) + cursor;
    }

    private static string Truncate(string body, int budget)
    {
        if (budget == int.MaxValue || body.Length <= budget)
        {
            return body;
        }

        var keep = budget - TruncationMarkerLength;
        if (keep <= 0)
        {
            return TruncationMarker[..budget];
        }

        return TruncationMarker + body[^keep..];
    }

    // Interim updates are best-effort. A non-cancellation throw means the
    // transport (sink) could not render this tick after its own resilience
    // (rate-limit backoff etc.) was exhausted; retain the buffer for the next
    // tick or the terminal call. Cancellation propagates.
    private async Task TryInterimUpdateAsync(
        IResponseSink sink,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.UpdateAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _options.Logger?.LogWarning(
                ex, "StreamPump interim update failed; buffer retained for next tick.");
        }
    }

    private sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
            {
                disposable.Dispose();
            }
        }
    }
}
