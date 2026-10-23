using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation.Abstractions;
using SlackNet;
using SlackNet.WebApi;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Scout.Adapter.Slack.Streaming;

/// <summary>
/// <see cref="IResponseSink"/> backed by a single Slack message in the
/// originating channel and thread. Begin posts a placeholder seeded with the
/// cursor and captures the (channel, ts) handle; Update edits the message with
/// the cumulative text plus cursor; Complete edits the final text without the
/// cursor and appends a citation footer; Fail appends an error footer to the
/// partial text. The transport write is behind delegate seams so the sink is
/// unit-testable without a real Slack connection.
///
/// This sink owns its transport resilience: writes are wrapped in a
/// <see cref="SlackRateLimitException"/> backoff-and-retry honouring Slack's
/// Retry-After, so the platform-agnostic pump never has to know about Slack's
/// rate limits.
///
/// Lifecycle is single-threaded by contract: exactly one Begin, then zero or
/// more Update, then one terminal Complete/Fail, all from the same caller. The
/// posted (channel, ts) handle is therefore written once in Begin and read by
/// later calls without synchronization.
///
/// Descriptive-write boundary: the sink can only ever target the (channel, ts)
/// handle captured from its own Begin post, which is itself bound to the
/// originating channel/thread. It can never write elsewhere.
/// </summary>
public sealed class SlackResponseSink : IResponseSink
{
    // Slack messages render comfortably well below the 40k hard limit; 3000 keeps
    // edits responsive and matches the locked contract for this adapter.
    internal const int MaxBodyLength = 3000;
    internal const string CursorGlyph = "▌"; // left half block, same as Discord.
    internal const string ErrorFooter = "\n\n(the AI provider returned an error; see Scout logs)";
    internal const string TruncationMarker = "... (truncated)\n\n";

    // Used when a rate-limit response carries no usable Retry-After hint.
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(1);

    private readonly Func<string, CancellationToken, Task<SlackMessageHandle>> _postInitial;
    private readonly Func<SlackMessageHandle, string, CancellationToken, Task> _edit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // Written once in Begin, read by later edits. Single-threaded by lifecycle.
    private SlackMessageHandle? _posted;
    private bool _begun;
    private bool _terminal;

    /// <summary>
    /// Production constructor. Posts and edits a message in
    /// <paramref name="channelId"/>, threaded under <paramref name="threadTs"/>
    /// when supplied (a reply stays in the originating thread).
    /// </summary>
    public SlackResponseSink(
        ISlackApiClient client,
        string channelId,
        string? threadTs = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("channelId must be non-empty.", nameof(channelId));
        }
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        _postInitial = async (body, ct) =>
        {
            var message = new Message
            {
                Channel = channelId,
                Text = body,
                ThreadTs = threadTs,
            };
            var response = await client.Chat.PostMessage(message, ct).ConfigureAwait(false);
            return new SlackMessageHandle(response.Channel, response.Ts);
        };

        _edit = async (handle, body, ct) =>
        {
            var update = new MessageUpdate
            {
                ChannelId = handle.ChannelId,
                Ts = handle.Ts,
                Text = body,
            };
            await client.Chat.Update(update, ct).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Test seam. <paramref name="postInitial"/> simulates the initial post and
    /// returns the message handle; <paramref name="edit"/> simulates each
    /// subsequent edit; both may throw <see cref="SlackRateLimitException"/> to
    /// exercise the backoff path.
    /// </summary>
    internal SlackResponseSink(
        Func<string, CancellationToken, Task<SlackMessageHandle>> postInitial,
        Func<SlackMessageHandle, string, CancellationToken, Task> edit,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _postInitial = postInitial ?? throw new ArgumentNullException(nameof(postInitial));
        _edit = edit ?? throw new ArgumentNullException(nameof(edit));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public RenderCapabilities Capabilities { get; } = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = MaxBodyLength,
        SupportsInterimCitations = false,
        EditIntervalFloor = TimeSpan.FromSeconds(1),
    };

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_begun)
        {
            throw new InvalidOperationException("BeginAsync called more than once.");
        }
        _begun = true;

        // Slack rejects empty message text; seed with the cursor. Begin failures
        // escape so the pump can report them.
        _posted = await PostWithRateLimitRetryAsync(CursorGlyph, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cumulativeText);
        EnsureActive();

        var body = WithCursor(cumulativeText);
        // Interim edits are best-effort. After exhausting the rate-limit retry,
        // a still-failing interim edit is dropped (the pump retains the buffer
        // and the terminal call will re-render the full body).
        try
        {
            await EditWithRateLimitRetryAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Slack interim edit failed; dropping this update.");
        }
    }

    public async Task CompleteAsync(
        string finalText,
        IReadOnlyList<Citation> citations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finalText);
        ArgumentNullException.ThrowIfNull(citations);
        EnterTerminal();

        var body = ComposeTerminalBody(finalText, citations);
        await EditWithRateLimitRetryAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(
        string finalTextSoFar,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finalTextSoFar);
        EnterTerminal();

        _logger.LogWarning("Slack response failed: {Reason}", reason);

        var body = finalTextSoFar.Length == 0
            ? ErrorFooter.TrimStart()
            : TruncateHead(finalTextSoFar, MaxBodyLength - ErrorFooter.Length)
                + ErrorFooter;

        await EditWithRateLimitRetryAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureActive()
    {
        if (!_begun)
        {
            throw new InvalidOperationException("UpdateAsync before BeginAsync.");
        }
        if (_terminal)
        {
            throw new InvalidOperationException("UpdateAsync after terminal call.");
        }
    }

    private void EnterTerminal()
    {
        if (_terminal)
        {
            throw new InvalidOperationException("Second terminal call.");
        }
        _terminal = true;
    }

    // Slack 429: back off for Retry-After (or a default) on the injected clock,
    // then retry once. Non-rate-limit exceptions propagate to the caller, which
    // decides whether to drop (interim) or surface (Begin).
    private async Task<SlackMessageHandle> PostWithRateLimitRetryAsync(
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _postInitial(body, cancellationToken).ConfigureAwait(false);
        }
        catch (SlackRateLimitException ex)
        {
            await BackoffAsync(ex, cancellationToken).ConfigureAwait(false);
            return await _postInitial(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EditWithRateLimitRetryAsync(string body, CancellationToken cancellationToken)
    {
        if (_posted is not { } handle)
        {
            return;
        }
        try
        {
            await _edit(handle, body, cancellationToken).ConfigureAwait(false);
        }
        catch (SlackRateLimitException ex)
        {
            await BackoffAsync(ex, cancellationToken).ConfigureAwait(false);
            await _edit(handle, body, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BackoffAsync(SlackRateLimitException ex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backoff = ex.RetryAfter ?? DefaultRateLimitBackoff;
        _logger.LogInformation(
            ex, "Slack rate-limited; backing off {Backoff} then retrying once.", backoff);
        await Task.Delay(backoff, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static string WithCursor(string body)
    {
        var budget = MaxBodyLength - CursorGlyph.Length;
        return TruncateHead(body, budget) + CursorGlyph;
    }

    // Build the terminal body keeping the answer head visible: if body+footer
    // overflows, drop the footer and keep the answer head with a visible marker,
    // so the user sees the start of the answer, not just the Sources footer.
    private static string ComposeTerminalBody(string finalText, IReadOnlyList<Citation> citations)
    {
        if (citations.Count == 0)
        {
            return TruncateHead(finalText, MaxBodyLength);
        }

        var footer = BuildCitationFooter(citations);
        if (finalText.Length + footer.Length <= MaxBodyLength)
        {
            return finalText + footer;
        }

        return TruncateHead(finalText, MaxBodyLength);
    }

    // Head-preserving truncation: keep the START of the text and mark the cut,
    // so the beginning of the answer is never lost.
    private static string TruncateHead(string body, int budget)
    {
        if (body.Length <= budget)
        {
            return body;
        }
        if (budget <= TruncationMarker.Length)
        {
            return body[..budget];
        }

        var keep = budget - TruncationMarker.Length;
        return body[..keep] + TruncationMarker;
    }

    internal static string BuildCitationFooter(IReadOnlyList<Citation> citations)
    {
        const string nl = "\n";
        var sb = new StringBuilder();
        sb.Append(nl);
        sb.Append(nl);
        sb.Append("Sources:").Append(nl);
        foreach (var citation in citations)
        {
            sb.Append("- ").Append(citation.ToString()).Append(nl);
        }
        return sb.ToString().TrimEnd();
    }
}
