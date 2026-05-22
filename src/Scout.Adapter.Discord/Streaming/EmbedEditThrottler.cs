using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Scout.Adapter.Discord.Streaming;

/// <summary>
/// Coalesces streamed text chunks into throttled edit operations against an
/// opaque edit delegate. Used by Discord streaming embed posters; the seam
/// is a plain <see cref="Func{T1, T2, TResult}"/> so this class can be unit
/// tested without any Discord.Net dependency.
/// </summary>
public sealed class EmbedEditThrottler : IAsyncDisposable
{
    // Truncation marker placed at the head of an over-long body. The buffer
    // tail is kept (recent text is more useful for the user than the head).
    private const string TruncationMarker = "... (truncated)\n\n";

    // Marker length is constant; cached for clarity at the truncation site.
    private static readonly int TruncationMarkerLength = TruncationMarker.Length;

    private readonly EmbedEditThrottlerOptions _options;
    private readonly SemaphoreSlim _editGate = new(1, 1);
    private readonly StringBuilder _buffer = new();
    private readonly object _bufferLock = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private DateTimeOffset? _lastEditAt;
    private int _editCount;
    private int _disposed;

    public EmbedEditThrottler(EmbedEditThrottlerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.EditInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "EditInterval must be non-negative.");
        }

        if (options.MaxLength <= TruncationMarkerLength + options.Cursor.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxLength must exceed truncation marker + cursor length.");
        }

        _options = options;
    }

    /// <summary>
    /// Append a streamed chunk. If the throttle interval has elapsed since
    /// the last edit (or this is the first edit) and no edit is in flight,
    /// schedules an edit with the current buffer plus the cursor. Multiple
    /// rapid chunks coalesce into the next edit.
    /// </summary>
    public async Task AppendAsync(string chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ThrowIfDisposed();

        if (chunk.Length > 0)
        {
            lock (_bufferLock)
            {
                _buffer.Append(chunk);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!ShouldEditNow())
        {
            return;
        }

        // Try to acquire the gate without blocking. If another edit is in
        // flight, just buffer; the next AppendAsync (or Finalize) picks up
        // the accumulated text.
        if (!await _editGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // Re-check inside the gate; another caller may have raced ahead.
            if (!ShouldEditNow())
            {
                return;
            }

            var body = RenderForStreaming();
            await TryEditWithRateLimitRetryAsync(body, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _editGate.Release();
        }
    }

    /// <summary>
    /// Final edit without the cursor. Always performed regardless of the
    /// throttle window. If the buffer is empty, edits with the empty-stream
    /// placeholder from <see cref="EmbedEditThrottlerOptions"/>.
    /// </summary>
    public Task FinalizeAsync(CancellationToken cancellationToken) =>
        FinalizeCoreAsync(errorFooter: null, cancellationToken);

    /// <summary>
    /// Like <see cref="FinalizeAsync"/> but appends an error footer. Used when
    /// the upstream stream throws mid-enumeration.
    /// </summary>
    public Task FinalizeWithErrorAsync(string errorFooter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errorFooter);
        return FinalizeCoreAsync(errorFooter, cancellationToken);
    }

    /// <summary>Inspection affordance for tests and diagnostics.</summary>
    public EmbedEditThrottlerSnapshot Snapshot()
    {
        int len;
        lock (_bufferLock)
        {
            len = _buffer.Length;
        }

        return new EmbedEditThrottlerSnapshot(len, Volatile.Read(ref _editCount), _lastEditAt);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await _disposeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already disposed CTS; nothing to do.
        }

        _disposeCts.Dispose();
        _editGate.Dispose();
    }

    private async Task FinalizeCoreAsync(string? errorFooter, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _editGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = RenderForFinalize(errorFooter);
            try
            {
                await TryEditWithRateLimitRetryAsync(body, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Finalize must not crash callers; log and swallow so partial
                // state is observable but the host stays alive.
                _options.Logger?.LogWarning(
                    ex,
                    "EmbedEditThrottler finalize edit failed after retries.");
            }
        }
        finally
        {
            if (_disposed == 0)
            {
                _editGate.Release();
            }
        }
    }

    private bool ShouldEditNow()
    {
        if (_lastEditAt is null)
        {
            return true;
        }

        var now = _options.TimeProvider.GetUtcNow();
        return now - _lastEditAt.Value >= _options.EditInterval;
    }

    private string RenderForStreaming()
    {
        string snapshot;
        lock (_bufferLock)
        {
            snapshot = _buffer.ToString();
        }

        var cursor = _options.Cursor;
        var budget = _options.MaxLength - cursor.Length;
        var truncated = Truncate(snapshot, budget);
        return truncated + cursor;
    }

    private string RenderForFinalize(string? errorFooter)
    {
        string snapshot;
        lock (_bufferLock)
        {
            snapshot = _buffer.ToString();
        }

        if (snapshot.Length == 0 && errorFooter is null)
        {
            return _options.EmptyStreamPlaceholder;
        }

        var withFooter = errorFooter is null
            ? snapshot
            : (snapshot.Length == 0 ? errorFooter : snapshot + errorFooter);

        return Truncate(withFooter, _options.MaxLength);
    }

    // Tail-preserving truncation: keep the most recent characters since the
    // user just streamed them in. Head is replaced with a marker so the UI
    // shows that some context was dropped.
    private static string Truncate(string body, int budget)
    {
        if (body.Length <= budget)
        {
            return body;
        }

        var keep = budget - TruncationMarkerLength;
        if (keep <= 0)
        {
            // Pathological budget; just return as much of the marker as fits.
            return TruncationMarker[..budget];
        }

        return TruncationMarker + body[^keep..];
    }

    private async Task TryEditWithRateLimitRetryAsync(string body, CancellationToken cancellationToken)
    {
        try
        {
            await _options.EditAsync(body, cancellationToken).ConfigureAwait(false);
            MarkEditCompleted();
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
                _options.Logger?.LogWarning(
                    ex,
                    "EmbedEditThrottler edit failed; buffer retained for next tick.");
                return;
            }

            _options.Logger?.LogInformation(
                "EmbedEditThrottler rate-limited; retrying after {RetryAfter}.",
                retryAfter.Value);

            try
            {
                await Task.Delay(retryAfter.Value, _options.TimeProvider, cancellationToken)
                    .ConfigureAwait(false);

                await _options.EditAsync(body, cancellationToken).ConfigureAwait(false);
                MarkEditCompleted();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception retryEx)
            {
                _options.Logger?.LogWarning(
                    retryEx,
                    "EmbedEditThrottler edit retry failed; buffer retained.");
            }
        }
    }

    private void MarkEditCompleted()
    {
        _lastEditAt = _options.TimeProvider.GetUtcNow();
        Interlocked.Increment(ref _editCount);
    }

    // Duck-typed extraction: Discord.Net's RateLimitedException carries a
    // RetryAfter as a numeric (seconds). We avoid a hard reference to that
    // assembly by reflecting on the type name and a RetryAfter property,
    // falling back to message text. Reflection is bounded to one lookup
    // per exception instance.
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
            "RetryAfter",
            BindingFlags.Instance | BindingFlags.Public);

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
                s,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) && parsed > 0 => TimeSpan.FromSeconds(parsed),
            _ => null,
        };
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(EmbedEditThrottler));
        }
    }
}
