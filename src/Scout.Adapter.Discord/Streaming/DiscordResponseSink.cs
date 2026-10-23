using System.Text;
using Discord;
using Discord.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord.Streaming;

/// <summary>
/// <see cref="IResponseSink"/> backed by a single Discord embed in the
/// originating channel. Begin posts an embed seeded with the cursor; Update
/// replaces the embed description with the cumulative text plus cursor;
/// Complete replaces it without the cursor and appends a citation footer; Fail
/// appends an error footer to the partial text. The transport edit is behind a
/// delegate seam so the sink is unit-testable without Discord.Net network.
///
/// This sink owns its transport resilience: writes are wrapped in a Discord.Net
/// <see cref="RateLimitedException"/> backoff-and-retry, so the platform-agnostic
/// pump never has to know about Discord's rate limits.
///
/// Lifecycle is single-threaded by contract: exactly one Begin, then zero or
/// more Update, then one terminal Complete/Fail, all from the same caller. The
/// production <c>posted</c> message handle is therefore written once in Begin
/// and read by later calls without synchronization.
/// </summary>
public sealed class DiscordResponseSink : IResponseSink
{
    internal const int MaxEmbedDescriptionLength = 4096;
    internal const string CursorGlyph = "▌";
    internal const string ErrorFooter = "\n\n(the AI provider returned an error; see Scout logs)";
    internal const string TruncationMarker = "... (truncated)\n\n";

    // Used when a rate-limit response carries no usable retry hint.
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(1);

    private readonly Func<string, CancellationToken, Task> _postInitial;
    private readonly Func<string, CancellationToken, Task> _edit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    private bool _begun;
    private bool _terminal;

    /// <summary>
    /// Production constructor. Posts and edits a Discord embed in
    /// <paramref name="channel"/>.
    /// </summary>
    public DiscordResponseSink(
        IMessageChannel channel,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        // Single-threaded by the sink lifecycle (Begin -> Update* -> terminal,
        // one caller): written once in the Begin closure, read by later edits.
        // No synchronization needed.
        IUserMessage? posted = null;

        _postInitial = async (body, ct) =>
        {
            var embed = new EmbedBuilder().WithDescription(body).Build();
            posted = await channel.SendMessageAsync(embed: embed, options: Request(ct))
                .ConfigureAwait(false);
        };

        _edit = async (body, ct) =>
        {
            if (posted is null)
            {
                return;
            }
            await posted.ModifyAsync(
                props => props.Embed = new EmbedBuilder().WithDescription(body).Build(),
                Request(ct)).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Test seam. <paramref name="postInitial"/> simulates the initial post and
    /// <paramref name="edit"/> simulates each subsequent embed edit; both may
    /// throw <see cref="RateLimitedException"/> to exercise the backoff path.
    /// </summary>
    internal DiscordResponseSink(
        Func<string, CancellationToken, Task> postInitial,
        Func<string, CancellationToken, Task> edit,
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
        MaxBodyLength = MaxEmbedDescriptionLength,
        SupportsInterimCitations = false,
    };

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_begun)
        {
            throw new InvalidOperationException("BeginAsync called more than once.");
        }
        _begun = true;

        // Discord rejects empty embed descriptions; seed with the cursor. Begin
        // failures escape so the pump can report them.
        await WriteWithRateLimitRetryAsync(_postInitial, CursorGlyph, cancellationToken)
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
            await WriteWithRateLimitRetryAsync(_edit, body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Discord interim edit failed; dropping this update.");
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
        await WriteWithRateLimitRetryAsync(_edit, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(
        string finalTextSoFar,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finalTextSoFar);
        EnterTerminal();

        _logger.LogWarning("Discord response failed: {Reason}", reason);

        var body = finalTextSoFar.Length == 0
            ? ErrorFooter.TrimStart()
            : TruncateHead(finalTextSoFar, MaxEmbedDescriptionLength - ErrorFooter.Length)
                + ErrorFooter;

        await WriteWithRateLimitRetryAsync(_edit, body, cancellationToken).ConfigureAwait(false);
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

    private static RequestOptions Request(CancellationToken cancellationToken)
        => new() { CancelToken = cancellationToken };

    // Discord 429: back off once and retry. Non-rate-limit exceptions propagate
    // to the caller, which decides whether to drop (interim) or surface (Begin).
    private async Task WriteWithRateLimitRetryAsync(
        Func<string, CancellationToken, Task> write,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            await write(body, cancellationToken).ConfigureAwait(false);
        }
        catch (RateLimitedException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation(
                ex, "Discord rate-limited; backing off {Backoff} then retrying once.",
                DefaultRateLimitBackoff);

            await Task.Delay(DefaultRateLimitBackoff, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            await write(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string WithCursor(string body)
    {
        var budget = MaxEmbedDescriptionLength - CursorGlyph.Length;
        return TruncateHead(body, budget) + CursorGlyph;
    }

    // Build the terminal body keeping the answer head visible: if body+footer
    // overflows, trim the footer first; if the answer alone still overflows,
    // preserve the HEAD with a visible marker (so the user sees the start of the
    // answer, not just the Sources footer).
    private static string ComposeTerminalBody(string finalText, IReadOnlyList<Citation> citations)
    {
        if (citations.Count == 0)
        {
            return TruncateHead(finalText, MaxEmbedDescriptionLength);
        }

        var footer = BuildCitationFooter(citations);
        if (finalText.Length + footer.Length <= MaxEmbedDescriptionLength)
        {
            return finalText + footer;
        }

        // Footer would push us over: drop the footer and keep the answer head.
        return TruncateHead(finalText, MaxEmbedDescriptionLength);
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
