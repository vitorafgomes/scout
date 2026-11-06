using System.Text;
using Discord;
using Discord.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord.Streaming;

/// <summary>
/// <see cref="IResponseSink"/> backed by Discord embeds in the channel the reply
/// belongs to. Begin posts an embed seeded with the cursor, as a reply to the
/// user's message when a reference is given, and shows the typing indicator
/// until the first streamed content. Every render splits the text into parts of
/// at most one embed (<see cref="DiscordReplySplitter"/>), edits the parts that
/// changed and posts new parts as follow-up messages in order, up to
/// <see cref="MaxParts"/>. Complete renders without the cursor and with a
/// citation footer; Fail appends an error footer to the partial text. The
/// transport is behind delegate seams so the sink is unit-testable without
/// Discord.Net network.
///
/// This sink owns its transport resilience: writes are wrapped in a Discord.Net
/// <see cref="RateLimitedException"/> backoff-and-retry, so the platform-agnostic
/// pump never has to know about Discord's rate limits.
///
/// Lifecycle is single-threaded by contract: exactly one Begin, then zero or
/// more Update, then one terminal Complete/Fail, all from the same caller.
/// Dispose stops the typing indicator if the reply never reached a terminal call.
/// </summary>
public sealed class DiscordResponseSink : IResponseSink, IDisposable
{
    internal const int MaxEmbedDescriptionLength = 4096;
    internal const int MaxParts = 5;
    internal const string CursorGlyph = "▌";
    internal const string ErrorFooter = "\n\n(the AI provider returned an error; see Scout logs)";
    internal const string IncompleteFooter = "\n\n(the rest of this reply could not be posted; see Scout logs)";

    // Room left in every part for the cursor, a closing fence, the truncation
    // marker and the incomplete footer.
    internal const int PartLength = MaxEmbedDescriptionLength - 128;

    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(1);

    private readonly Func<int, string, CancellationToken, Task> _post;
    private readonly Func<int, string, CancellationToken, Task> _edit;
    private readonly Func<IDisposable> _enterTyping;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly List<string> _rendered = [];

    private IDisposable? _typing;
    private bool _begun;
    private bool _terminal;

    /// <summary>
    /// Production constructor. Posts and edits Discord embeds in
    /// <paramref name="channel"/>; the first post replies to
    /// <paramref name="replyTo"/> when it is set.
    /// </summary>
    public DiscordResponseSink(
        IMessageChannel channel,
        MessageReference? replyTo = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;

        var posted = new List<IUserMessage>();

        _post = async (index, body, ct) =>
        {
            var message = await channel.SendMessageAsync(
                    embed: Embed(body),
                    options: Request(ct),
                    messageReference: index == 0 ? replyTo : null)
                .ConfigureAwait(false);
            posted.Add(message);
        };

        _edit = (index, body, ct) =>
            posted[index].ModifyAsync(props => props.Embed = Embed(body), Request(ct));

        _enterTyping = () => channel.EnterTypingState();
    }

    /// <summary>
    /// Test seam. <paramref name="post"/> simulates posting part <c>index</c> as a
    /// new message and <paramref name="edit"/> simulates editing the message of
    /// part <c>index</c>; both may throw <see cref="RateLimitedException"/> to
    /// exercise the backoff path. <paramref name="enterTyping"/> starts the typing
    /// indicator and returns the handle that stops it.
    /// </summary>
    internal DiscordResponseSink(
        Func<int, string, CancellationToken, Task> post,
        Func<int, string, CancellationToken, Task> edit,
        Func<IDisposable>? enterTyping = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _edit = edit ?? throw new ArgumentNullException(nameof(edit));
        _enterTyping = enterTyping ?? (() => NoTyping.Instance);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// No body limit for the pump: the sink splits a long reply across messages
    /// itself and caps it at <see cref="MaxParts"/> embeds.
    /// </summary>
    public RenderCapabilities Capabilities { get; } = new()
    {
        FineGrainedEdit = true,
        MaxBodyLength = int.MaxValue,
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
        await RenderAsync([CursorGlyph], cancellationToken).ConfigureAwait(false);
        try
        {
            _typing = _enterTyping();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord typing indicator could not start; replying without it.");
        }
    }

    public async Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cumulativeText);
        EnsureActive();

        var text = cumulativeText.EndsWith(CursorGlyph, StringComparison.Ordinal)
            ? cumulativeText[..^CursorGlyph.Length]
            : cumulativeText;
        if (!string.IsNullOrWhiteSpace(text))
        {
            StopTyping();
        }

        var parts = DiscordReplySplitter.Split(text, PartLength, MaxParts).ToList();
        if (parts.Count == 0)
        {
            parts.Add(string.Empty);
        }
        parts[^1] += CursorGlyph;

        // Interim edits are best-effort. After exhausting the rate-limit retry,
        // a still-failing interim write is dropped; the next render retries it.
        try
        {
            await RenderAsync(parts, cancellationToken).ConfigureAwait(false);
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

        var body = citations.Count == 0 ? finalText : finalText + BuildCitationFooter(citations);
        var parts = DiscordReplySplitter.Split(body, PartLength, MaxParts);
        try
        {
            await RenderAsync(parts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && FirstUnwrittenPart(parts) > 0)
        {
            var last = FirstUnwrittenPart(parts) - 1;
            _logger.LogWarning(
                ex, "Discord could not write part {Part} of a reply; marking the reply incomplete.", last + 2);
            await WriteWithRateLimitRetryAsync(_edit, last, parts[last] + IncompleteFooter, cancellationToken)
                .ConfigureAwait(false);
        }
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
            : finalTextSoFar + ErrorFooter;

        await RenderAsync(DiscordReplySplitter.Split(body, PartLength, MaxParts), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Stops the typing indicator.</summary>
    public void Dispose() => StopTyping();

    private int FirstUnwrittenPart(IReadOnlyList<string> parts)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            if (i >= _rendered.Count || _rendered[i] != parts[i])
            {
                return i;
            }
        }
        return parts.Count;
    }

    // Writes the parts that differ from what is on screen, in order, and stops
    // at the first failure so parts never arrive out of order.
    private async Task RenderAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            if (i < _rendered.Count)
            {
                if (_rendered[i] == parts[i])
                {
                    continue;
                }
                await WriteWithRateLimitRetryAsync(_edit, i, parts[i], cancellationToken).ConfigureAwait(false);
                _rendered[i] = parts[i];
            }
            else
            {
                await WriteWithRateLimitRetryAsync(_post, i, parts[i], cancellationToken).ConfigureAwait(false);
                _rendered.Add(parts[i]);
            }
        }
    }

    private void StopTyping()
    {
        _typing?.Dispose();
        _typing = null;
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
        StopTyping();
    }

    private static Embed Embed(string body) => new EmbedBuilder().WithDescription(body).Build();

    private static RequestOptions Request(CancellationToken cancellationToken)
        => new() { CancelToken = cancellationToken };

    // Discord 429: back off once and retry. Non-rate-limit exceptions propagate
    // to the caller, which decides whether to drop (interim) or surface (Begin).
    private async Task WriteWithRateLimitRetryAsync(
        Func<int, string, CancellationToken, Task> write,
        int index,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            await write(index, body, cancellationToken).ConfigureAwait(false);
        }
        catch (RateLimitedException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation(
                ex, "Discord rate-limited; backing off {Backoff} then retrying once.",
                DefaultRateLimitBackoff);

            await Task.Delay(DefaultRateLimitBackoff, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            await write(index, body, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string BuildCitationFooter(IReadOnlyList<Citation> citations)
    {
        var sb = new StringBuilder();
        sb.Append('\n');
        sb.Append('\n');
        sb.Append("Sources:").Append('\n');
        foreach (var citation in citations)
        {
            sb.Append("- ").Append(citation.ToString()).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private sealed class NoTyping : IDisposable
    {
        public static readonly NoTyping Instance = new();

        public void Dispose()
        {
        }
    }
}
