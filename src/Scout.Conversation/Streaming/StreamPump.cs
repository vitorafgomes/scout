using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Scout.Conversation.Abstractions;

namespace Scout.Conversation.Streaming;

/// <summary>
/// Transport-agnostic stream-drive loop. Owns the response lifecycle once:
/// opens the sink, accumulates tokens into a cumulative buffer, throttles
/// interim <see cref="IResponseSink.UpdateAsync"/> calls per the sink's
/// capabilities, then issues a single terminal Complete or Fail. The sink stays
/// a thin transport. Cadence and truncation derive from
/// <see cref="StreamPumpOptions"/> tightened by <see cref="RenderCapabilities"/>,
/// so the pump is ignorant of which platform it drives.
/// </summary>
public sealed class StreamPump
{
    // Tail-preserving truncation marker (recent text is more useful than head).
    private const string TruncationMarker = "... (truncated)\n\n";
    private static readonly int TruncationMarkerLength = TruncationMarker.Length;

    private readonly StreamPumpOptions _options;

    public StreamPump(StreamPumpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.EditInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "EditInterval must be non-negative.");
        }

        _options = options;
    }

    /// <summary>
    /// Drive <paramref name="response"/> into <paramref name="sink"/>. Begins
    /// the sink, streams updates, and finishes with exactly one terminal call.
    /// Caller cancellation propagates as <see cref="OperationCanceledException"/>
    /// without forcing a terminal edit; any other mid-stream failure is reported
    /// via <see cref="IResponseSink.FailAsync"/> and does not escape.
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
        var interimEdits = caps.FineGrainedEdit || caps.SupportsInterimCitations;

        using var linked = LinkTimeout(cancellationToken, out var pumpToken);

        await sink.BeginAsync(cancellationToken).ConfigureAwait(false);

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

                if (!interimEdits)
                {
                    continue;
                }

                if (!ShouldEditNow(lastEditAt, interval))
                {
                    continue;
                }

                var body = RenderInterim(buffer.ToString(), maxLength);
                await UpdateWithRateLimitRetryAsync(sink, body, pumpToken).ConfigureAwait(false);
                lastEditAt = _options.TimeProvider.GetUtcNow();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled: surface it, do not force a terminal edit.
            throw;
        }
        catch (Exception ex)
        {
            // Mid-stream failure (including overall-timeout): finalize with the
            // partial buffer and an error reason. Must not escape into the host.
            var reason = ex is OperationCanceledException ? "stream timed out" : ex.Message;
            _options.Logger?.LogWarning(
                ex, "StreamPump stream failed mid-flight; finalizing with partial content.");
            await sink.FailAsync(buffer.ToString(), reason, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var finalText = buffer.Length == 0
            ? _options.EmptyStreamPlaceholder
            : Truncate(buffer.ToString(), maxLength);

        await sink.CompleteAsync(finalText, response.Citations, cancellationToken)
            .ConfigureAwait(false);
    }

    private IDisposable? LinkTimeout(CancellationToken caller, out CancellationToken pumpToken)
    {
        if (_options.OverallTimeout is not { } timeout)
        {
            pumpToken = caller;
            return null;
        }

        // Deterministic timeout via the injected clock: a TimeProvider-backed
        // CTS fires the timeout, linked with the caller token.
        var timeoutCts = new CancellationTokenSource(timeout, _options.TimeProvider);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(caller, timeoutCts.Token);
        pumpToken = linkedCts.Token;
        return new CompositeDisposable(linkedCts, timeoutCts);
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

    private async Task UpdateWithRateLimitRetryAsync(
        IResponseSink sink,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.UpdateAsync(body, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var retryAfter = TryExtractRetryAfter(ex);
            if (retryAfter is null)
            {
                // Non-rate-limit transient: drop this interim edit, keep the
                // buffer for the next tick / finalize. Does not escape.
                _options.Logger?.LogWarning(
                    ex, "StreamPump interim update failed; buffer retained for next tick.");
                return;
            }

            _options.Logger?.LogInformation(
                "StreamPump rate-limited; retrying after {RetryAfter}.", retryAfter.Value);

            try
            {
                await Task.Delay(retryAfter.Value, _options.TimeProvider, cancellationToken)
                    .ConfigureAwait(false);
                await sink.UpdateAsync(body, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception retryEx)
            {
                _options.Logger?.LogWarning(
                    retryEx, "StreamPump interim update retry failed; buffer retained.");
            }
        }
    }

    // Duck-typed extraction. Discord.Net's RateLimitedException carries a
    // RetryAfter; we avoid a hard reference by reflecting on the type name and a
    // RetryAfter property, falling back to message text.
    internal static TimeSpan? TryExtractRetryAfter(Exception ex)
    {
        var typeName = ex.GetType().Name;
        var looksRateLimited =
            typeName.Contains("RateLimit", StringComparison.OrdinalIgnoreCase)
            || (ex.Message?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ?? false);

        if (!looksRateLimited)
        {
            return null;
        }

        var prop = ex.GetType().GetProperty(
            "RetryAfter", BindingFlags.Instance | BindingFlags.Public);
        if (prop is null)
        {
            return null;
        }

        var value = prop.GetValue(ex);
        return value switch
        {
            TimeSpan ts when ts > TimeSpan.Zero => ts,
            double secs when secs > 0 => TimeSpan.FromSeconds(secs),
            float secs when secs > 0 => TimeSpan.FromSeconds(secs),
            int secs when secs > 0 => TimeSpan.FromSeconds(secs),
            long secs when secs > 0 => TimeSpan.FromSeconds(secs),
            string s when double.TryParse(
                s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0 => TimeSpan.FromSeconds(parsed),
            _ => null,
        };
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
