using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Contracts;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Telemetry;

namespace Scout.Conversation;

/// <summary>
/// Adapter-facing handler: routes an inbound message to a streaming response
/// and pumps it into the channel-bound sink. Wraps the work in an OTel activity.
/// Failures surface through the sink's <see cref="IResponseSink.FailAsync"/> and
/// never crash the host.
///
/// This is also the single choke point where the caller's
/// <see cref="ScoutRequestIdentity"/> is made ambient for the turn: the scope
/// opened at the top of <see cref="HandleAsync"/> encloses BOTH the router call
/// and the pump, so any Semantic Kernel plugin function the kernel invokes while
/// the response streams observes the identity. The scope relies on
/// <see cref="AsyncLocalScoutRequestContext"/> flowing over the ExecutionContext;
/// <c>ConfigureAwait(false)</c> deep in the pump/gateway does NOT drop it.
/// </summary>
public sealed class ConversationConductor
{
    private readonly IConversationRouter _router;
    private readonly StreamPump _pump;
    private readonly IScoutRequestContext _requestContext;
    private readonly ILogger<ConversationConductor> _logger;

    public ConversationConductor(
        IConversationRouter router,
        StreamPump pump,
        IScoutRequestContext requestContext,
        ILogger<ConversationConductor>? logger = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
        _requestContext = requestContext ?? throw new ArgumentNullException(nameof(requestContext));
        _logger = logger ?? NullLogger<ConversationConductor>.Instance;
    }

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

        // Make the caller's identity ambient for the whole turn. The scope encloses
        // both the router call and the pump so the lazily enumerated ChatResponse —
        // which is where Semantic Kernel invokes plugin functions — runs inside it.
        // The value rides the ExecutionContext, so the ConfigureAwait(false) below
        // (and deeper in the pump/gateway) does NOT drop it. Identity gates output
        // disclosure only; it never authorizes a write.
        using var identityScope = _requestContext.BeginScope(new ScoutRequestIdentity(
            message.UserId, message.ChannelId, message.IsDirectMessage, message.AdapterName));

        try
        {
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
}
