using System.Globalization;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Streaming;
using Scout.Llm.Abstractions;

namespace Scout.Adapter.Local;

/// <summary>
/// Translates the final lines of one live session off the transcription path: one translation in flight, newer finals
/// coalescing so only the newest waits and the ones it replaced are reported as skipped, the two previous finals as
/// context, at most 20 translations a minute, a 20 s budget per call, and a failure reported for its line without
/// ending the session. Without a translator it reports once that there is no AI Source and translates nothing.
/// </summary>
internal sealed class LiveTranslationQueue
{
    internal const int MaxPerMinute = 20;
    internal const string SkippedError = "skipped";
    internal const string PausedError = "Translation paused: more than 20 lines a minute.";
    internal const string NoSourceError = "Scout has no AI Source for translation; connect one in AI Sources.";
    internal const string FailedError = "Scout could not translate that line; see Scout logs.";
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static readonly StreamPumpOptions Sentences = new();

    private readonly string _id;
    private readonly ILiveTranslator? _translator;
    private readonly Task<string> _language;
    private readonly Action<LiveTranslation> _emit;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closing;
    private readonly object _gate = new();
    private readonly List<string> _finals = [];
    private readonly List<int> _skipped = [];
    private readonly Queue<DateTimeOffset> _started = new();
    private Line? _pending;
    private Task? _pump;
    private bool _running;
    private bool _paused;
    private int _drained;
    private string? _resolved;

    public LiveTranslationQueue(
        string id,
        ILiveTranslator? translator,
        Task<string> language,
        Action<LiveTranslation> emit,
        TimeProvider time,
        ILogger logger,
        CancellationToken lifetime)
    {
        _id = id;
        _translator = translator;
        _language = language;
        _emit = emit;
        _time = time;
        _logger = logger;
        _closing = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        if (translator is null)
        {
            _pump = Task.Run(ReportNoSourceAsync, CancellationToken.None);
        }
    }

    /// <summary>Queue the final line <paramref name="seq"/>; never waits for a translation.</summary>
    public void Enqueue(int seq, string text)
    {
        if (_translator is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_closing.IsCancellationRequested || Volatile.Read(ref _drained) == 1)
            {
                _logger.LogDebug("Live translation {Id} seq {Seq} refused: the session is ending.", _id, seq);
                return;
            }

            IReadOnlyList<string> context = [.. _finals];
            _finals.Add(text);
            if (_finals.Count > 2)
            {
                _finals.RemoveAt(0);
            }

            if (_pending is { } replaced)
            {
                _skipped.Add(replaced.Seq);
            }

            _pending = new Line(seq, text, context);
            if (!_running)
            {
                _running = true;
                _pump = Task.Run(PumpAsync, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Wait at most <paramref name="max"/> for the line in flight and the one waiting, then stop; nothing is emitted
    /// once this completes, and a line left untranslated is reported as skipped.
    /// </summary>
    public async Task DrainAsync(TimeSpan max)
    {
        if (Interlocked.Exchange(ref _drained, 1) == 1)
        {
            return;
        }

        Task? pump;
        bool paused;
        lock (_gate)
        {
            pump = _pump;
            paused = _paused;
        }

        if (pump is not null && max > TimeSpan.Zero && !paused)
        {
            try
            {
                await pump.WaitAsync(max, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation("Live translation {Id} drained after {Seconds} s with a translation still running.", _id, max.TotalSeconds);
            }
        }

        await _closing.CancelAsync().ConfigureAwait(false);
        if (pump is not null)
        {
            await pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        int[] left;
        lock (_gate)
        {
            left = [.. _skipped, .. _pending is { } pending ? [pending.Seq] : Array.Empty<int>()];
            _skipped.Clear();
            _pending = null;
        }

        if (_resolved is { } language)
        {
            foreach (var seq in left)
            {
                Emit(new LiveTranslation(seq, language, null, SkippedError));
            }
        }

        _closing.Dispose();
    }

    private async Task ReportNoSourceAsync()
    {
        try
        {
            var language = await _language.WaitAsync(_closing.Token).ConfigureAwait(false);
            _logger.LogInformation("Live translation {Id} to {Language} is off: no AI Source is connected.", _id, language);
            Emit(new LiveTranslation(0, language, null, NoSourceError));
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            var language = _resolved ??= await _language.WaitAsync(_closing.Token).ConfigureAwait(false);
            while (true)
            {
                lock (_gate)
                {
                    if (_pending is null)
                    {
                        _running = false;
                        return;
                    }
                }

                await WaitForRoomAsync(language).ConfigureAwait(false);

                Line line;
                lock (_gate)
                {
                    line = _pending!;
                    _pending = null;
                    _paused = false;
                    _started.Enqueue(_time.GetUtcNow());
                }

                EmitSkipped(language);
                await TranslateAsync(line, language).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _running = false;
            }

            _logger.LogError("Live translation {Id} stopped: {Error}.", _id, ex.GetType().Name);
        }
    }

    private async Task WaitForRoomAsync(string language)
    {
        while (true)
        {
            TimeSpan wait;
            bool note;
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                while (_started.Count > 0 && now - _started.Peek() >= Window)
                {
                    _started.Dequeue();
                }

                if (_started.Count < MaxPerMinute)
                {
                    return;
                }

                wait = _started.Peek() + Window - now;
                note = !_paused;
                _paused = true;
            }

            var delay = Task.Delay(wait, _time, _closing.Token);
            if (note)
            {
                _logger.LogInformation("Live translation {Id} paused: {Count} lines in the last minute.", _id, MaxPerMinute);
                Emit(new LiveTranslation(0, language, null, PausedError));
            }

            await delay.ConfigureAwait(false);
        }
    }

    private async Task TranslateAsync(Line line, string language)
    {
        using var timeout = new CancellationTokenSource(CallTimeout, _time);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, timeout.Token);
        try
        {
            var text = await _translator!.TranslateAsync(line.Text, line.Context, language, call.Token)
                .WaitAsync(call.Token)
                .ConfigureAwait(false);
            _logger.LogDebug(
                "Live translation {Id} seq {Seq} to {Language} answered (chars={Length}).", _id, line.Seq, language, text.Length);
            Emit(new LiveTranslation(line.Seq, language, text, null));
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            lock (_gate)
            {
                _skipped.Add(line.Seq);
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Live translation {Id} seq {Seq} to {Language} failed: {Error}.", _id, line.Seq, language, ex.GetType().Name);
            Emit(new LiveTranslation(line.Seq, language, null, Sentence(ex)));
        }
    }

    private static string Sentence(Exception ex) => ex switch
    {
        LlmRateLimitException => Sentences.RateLimitMessage,
        LlmQuotaExceededException { Code: "credit_balance_too_low" } => Sentences.CreditExhaustedMessage,
        LlmQuotaExceededException => Sentences.QuotaExceededMessage,
        LlmSignInRequiredException => Sentences.SignInRequiredMessage,
        LlmModelNotFoundException missing => string.Format(CultureInfo.InvariantCulture, Sentences.ModelNotFoundMessage, missing.Model),
        _ => FailedError,
    };

    private void EmitSkipped(string language)
    {
        int[] skipped;
        lock (_gate)
        {
            skipped = [.. _skipped];
            _skipped.Clear();
        }

        foreach (var seq in skipped)
        {
            _logger.LogInformation("Live translation {Id} seq {Seq} to {Language} skipped for a newer line.", _id, seq, language);
            Emit(new LiveTranslation(seq, language, null, SkippedError));
        }
    }

    private void Emit(LiveTranslation translation)
    {
        try
        {
            _emit(translation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Live translation {Id} seq {Seq} could not be written: {Error}.", _id, translation.Seq, ex.GetType().Name);
        }
    }

    private sealed record Line(int Seq, string Text, IReadOnlyList<string> Context);
}

/// <summary>One translation event: the translated <paramref name="Text"/> of line <paramref name="Seq"/>, or an <paramref name="Error"/>.</summary>
internal sealed record LiveTranslation(int Seq, string Language, string? Text, string? Error);
