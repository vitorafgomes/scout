using System.Text;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams.Streaming;

/// <summary>
/// <see cref="IResponseSink"/> over the Teams streaming protocol, bound to a
/// single originating conversation (the descriptive-write boundary). The Teams
/// shape is the reverse of Discord's "edit one message": there is no message
/// handle to mutate. Instead the sink emits a sequence of activities that the
/// Teams client stitches into one growing bubble:
///
/// <list type="number">
///   <item>Begin -> one <c>typing</c> activity, streamType <c>informative</c>,
///   streamSequence 1. Its resource id becomes the <c>streamId</c> echoed by
///   every later activity in the stream.</item>
///   <item>Update -> a <c>typing</c> activity, streamType <c>streaming</c>, with
///   the FULL CUMULATIVE text (never a delta) and a monotonically increasing
///   streamSequence. The contract's cumulative <c>UpdateAsync</c> maps 1:1 onto
///   these cumulative typing activities.</item>
///   <item>Complete -> one <c>message</c> activity, streamType <c>final</c>,
///   carrying the final text, citation entities, and attachments. Terminal.</item>
///   <item>Fail -> one <c>message</c> activity, streamType <c>final</c>, carrying
///   the partial text plus an error note. Terminal.</item>
/// </list>
///
/// Citations land only on the final activity (<c>SupportsInterimCitations</c> is
/// false). The send is behind <see cref="ITeamsActivitySender"/> so the whole
/// lifecycle is unit-testable without Azure. The sink owns its transport
/// resilience: 429 backs off once and retries; a 403 ContentStreamNotAllowed
/// terminates the stream as canceled. The platform-agnostic pump stays ignorant
/// of both.
///
/// Lifecycle is single-threaded by contract (one Begin, then zero or more
/// Update, then one terminal Complete/Fail, from one caller), so the stream id
/// and sequence counter are written/read without synchronization.
/// </summary>
public sealed class TeamsResponseSink : IResponseSink
{
    // Teams message bodies cap well above a typical answer; this bounds the
    // rendered body so the pump truncates before the service rejects an
    // over-long activity. Conservative relative to the documented ceiling.
    internal const int MaxTeamsBodyLength = 28_000;

    internal const string StreamInfoEntityType = "streaminfo";
    internal const string StreamTypeInformative = "informative";
    internal const string StreamTypeStreaming = "streaming";
    internal const string StreamTypeFinal = "final";
    internal const string InformativeText = "Working on it...";
    internal const string ErrorFooter = "\n\n_(response interrupted)_";

    // Teams tolerates ~1 streaming activity per second; the pump's EditIntervalFloor
    // already throttles to this, so the sink only needs to back off on an explicit
    // 429 with no usable retry hint.
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(1);

    private readonly ITeamsActivitySender _sender;
    private readonly bool _streamingAllowed;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    private bool _begun;
    private bool _terminal;
    private bool _streamCanceled;
    private string? _streamId;
    private int _sequence;

    /// <summary>
    /// Create a sink for one conversation.
    /// </summary>
    /// <param name="sender">Transport seam that delivers each activity.</param>
    /// <param name="streamingSupported">
    /// False for group chats / channels, where Teams does not honour streaming
    /// typing activities. The sink then renders final-only: Begin and Update are
    /// no-ops on the wire and the terminal activity carries the whole answer.
    /// </param>
    /// <param name="timeProvider">Clock seam for deterministic backoff in tests.</param>
    /// <param name="logger">Diagnostics for backoff and terminal failures.</param>
    public TeamsResponseSink(
        ITeamsActivitySender sender,
        bool streamingSupported = true,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _streamingAllowed = streamingSupported;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public RenderCapabilities Capabilities { get; } = new()
    {
        // Teams CAN edit the growing bubble via cumulative streaming activities,
        // so interim updates are worthwhile and the pump should send them.
        FineGrainedEdit = true,
        MaxBodyLength = MaxTeamsBodyLength,
        // Citations only on the final message activity.
        SupportsInterimCitations = false,
        // Teams tolerates ~1 streaming activity per second.
        EditIntervalFloor = TimeSpan.FromSeconds(1),
        // Teams closes a streamed response after ~2 minutes; the pump folds this
        // with its own OverallTimeout (min wins) and finalizes by then.
        MaxResponseDuration = TimeSpan.FromSeconds(120),
    };

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_begun)
        {
            throw new InvalidOperationException("BeginAsync called more than once.");
        }
        _begun = true;

        if (!_streamingAllowed)
        {
            // Group/channel: nothing streams; the terminal activity carries all.
            return;
        }

        _sequence = 1;
        var informative = BuildTypingActivity(StreamTypeInformative, _sequence, InformativeText);

        // The informative activity's resource id is the stream id every later
        // activity must echo. Begin failures escape so the pump can report them,
        // EXCEPT a 403 ContentStreamNotAllowed which downgrades to final-only.
        try
        {
            _streamId = await SendWithResilienceAsync(informative, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TeamsTransportException ex) when (ex.IsContentStreamNotAllowed)
        {
            HandleContentStreamNotAllowed(ex);
        }
    }

    public async Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cumulativeText);
        EnsureActive();

        if (!_streamingAllowed || _streamCanceled)
        {
            // Final-only mode (group, or stream was canceled by the service):
            // swallow interim updates; the terminal call renders the full body.
            return;
        }

        _sequence++;
        var body = Truncate(cumulativeText, MaxTeamsBodyLength);
        var activity = BuildTypingActivity(StreamTypeStreaming, _sequence, body);

        // Interim updates are best-effort. After the sink's own 429 retry, a
        // still-failing interim is dropped (the pump keeps the buffer and the
        // terminal activity re-renders the full body). A mid-stream 403
        // ContentStreamNotAllowed cancels streaming for the rest of the run.
        try
        {
            await SendWithResilienceAsync(activity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TeamsTransportException ex) when (ex.IsContentStreamNotAllowed)
        {
            HandleContentStreamNotAllowed(ex);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Teams interim streaming activity failed; dropping this update.");
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

        var body = Truncate(finalText, MaxTeamsBodyLength);
        var activity = BuildFinalActivity(body, citations);
        await SendWithResilienceAsync(activity, cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(
        string finalTextSoFar,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finalTextSoFar);
        EnterTerminal();

        _logger.LogWarning("Teams response failed: {Reason}", reason);

        var partial = Truncate(finalTextSoFar, MaxTeamsBodyLength - ErrorFooter.Length);
        var body = partial.Length == 0 ? ErrorFooter.TrimStart() : partial + ErrorFooter;
        var activity = BuildFinalActivity(body, Array.Empty<Citation>());
        await SendWithResilienceAsync(activity, cancellationToken).ConfigureAwait(false);
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

    // A 403 ContentStreamNotAllowed means the service refuses streaming for this
    // conversation. Per the boundary contract this is terminal-canceled for the
    // stream: stop emitting typing activities and let the single final message
    // (Complete/Fail) carry the answer. Modelled as cancellation of the stream,
    // not of the whole response, so the user still gets a final bubble.
    private void HandleContentStreamNotAllowed(TeamsTransportException ex)
    {
        _streamCanceled = true;
        _streamId = null;
        _logger.LogWarning(
            ex, "Teams refused content streaming ({Code}); falling back to a single final message.",
            TeamsTransportException.ContentStreamNotAllowed);
    }

    // 429: back off once on the injected clock and retry. ContentStreamNotAllowed
    // and other transport errors propagate to the caller, which decides whether to
    // drop (interim), downgrade (Begin), or surface.
    private async Task<string?> SendWithResilienceAsync(
        Activity activity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _sender.SendAsync(activity, cancellationToken).ConfigureAwait(false);
        }
        catch (TeamsTransportException ex) when (ex.IsRateLimited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation(
                ex, "Teams rate-limited (429); backing off {Backoff} then retrying once.",
                DefaultRateLimitBackoff);

            await Task.Delay(DefaultRateLimitBackoff, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            return await _sender.SendAsync(activity, cancellationToken).ConfigureAwait(false);
        }
    }

    private Activity BuildTypingActivity(string streamType, int sequence, string text)
    {
        var activity = Activity.CreateTypingActivity() as Activity
            ?? throw new InvalidOperationException("Failed to create typing activity.");
        activity.Text = text;
        activity.Entities = [BuildStreamInfoEntity(streamType, sequence)];
        return activity;
    }

    private Activity BuildFinalActivity(string text, IReadOnlyList<Citation> citations)
    {
        var activity = Activity.CreateMessageActivity() as Activity
            ?? throw new InvalidOperationException("Failed to create message activity.");
        activity.Text = text;

        // A `final` streaminfo entity is only valid when it closes a stream that
        // was actually opened: it must carry the same streamId. When no stream
        // ran (group/channel) or it was canceled (403 ContentStreamNotAllowed),
        // emit a plain message with no streaminfo entity and no dangling sequence.
        var streamWasOpen = _streamId is not null && !_streamCanceled;

        var entities = new List<Entity>();
        if (streamWasOpen)
        {
            // The final activity's sequence must stay monotonic with the interim
            // streaming activities, so advance past the last one.
            _sequence++;
            entities.Add(BuildStreamInfoEntity(StreamTypeFinal, _sequence));
        }
        if (citations.Count > 0)
        {
            entities.Add(BuildCitationEntity(citations));
            activity.Attachments = BuildCitationAttachments(citations);
        }
        activity.Entities = entities;
        return activity;
    }

    // The streaminfo entity carries the streamType, the monotonic streamSequence,
    // and (after Begin) the streamId tying every activity to the same bubble.
    private Entity BuildStreamInfoEntity(string streamType, int sequence)
    {
        var entity = new Entity { Type = StreamInfoEntityType };
        entity.Properties["streamType"] = streamType;
        entity.Properties["streamSequence"] = sequence;
        if (!string.IsNullOrEmpty(_streamId))
        {
            entity.Properties["streamId"] = _streamId;
        }
        return entity;
    }

    // Citations render as a message entity (Teams "AI message" citation shape):
    // a numbered list the client can surface as references on the final bubble.
    internal static Entity BuildCitationEntity(IReadOnlyList<Citation> citations)
    {
        var entity = new Entity { Type = "https://schema.org/Message" };
        entity.Properties["@type"] = "Message";
        entity.Properties["@context"] = "https://schema.org";

        var refs = new List<object>(citations.Count);
        for (var i = 0; i < citations.Count; i++)
        {
            var c = citations[i];
            var appearance = new Dictionary<string, object>
            {
                ["@type"] = "DigitalDocument",
                ["name"] = c.Label,
            };
            if (!string.IsNullOrWhiteSpace(c.Url))
            {
                appearance["url"] = c.Url!;
            }
            if (!string.IsNullOrWhiteSpace(c.Snippet))
            {
                appearance["abstract"] = c.Snippet!;
            }
            refs.Add(new Dictionary<string, object>
            {
                ["@type"] = "Claim",
                ["position"] = i + 1,
                ["appearance"] = appearance,
            });
        }

        entity.Properties["citation"] = JToken.FromObject(refs);
        return entity;
    }

    // Lightweight HeroCard-style attachment listing the sources, so clients that
    // ignore the citation entity still show the references on the final message.
    internal static List<Attachment> BuildCitationAttachments(IReadOnlyList<Citation> citations)
    {
        var sb = new StringBuilder();
        sb.Append("**Sources:**\n");
        foreach (var citation in citations)
        {
            sb.Append("- ").Append(citation.ToString()).Append('\n');
        }

        return
        [
            new Attachment
            {
                ContentType = "application/vnd.microsoft.card.adaptive",
                Content = new Dictionary<string, object>
                {
                    ["type"] = "AdaptiveCard",
                    ["version"] = "1.4",
                    ["body"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "TextBlock",
                            ["wrap"] = true,
                            ["text"] = sb.ToString().TrimEnd(),
                        },
                    },
                },
            },
        ];
    }

    // Head-preserving truncation: keep the START of the answer (most relevant
    // for a streamed reply) and mark the cut.
    private static string Truncate(string body, int budget)
    {
        if (budget <= 0)
        {
            return string.Empty;
        }
        if (body.Length <= budget)
        {
            return body;
        }

        const string marker = "... (truncated)";
        if (budget <= marker.Length)
        {
            return body[..budget];
        }

        var keep = budget - marker.Length;
        return body[..keep] + marker;
    }
}
