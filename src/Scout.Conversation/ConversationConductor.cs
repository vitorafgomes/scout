using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Conversation.Telemetry;

namespace Scout.Conversation;

/// <summary>
/// Adapter-facing handler: routes an inbound message to a streaming response
/// and pumps it into the channel-bound sink. Wraps the work in an OTel activity.
/// Failures surface through the sink's <see cref="IResponseSink.FailAsync"/> and
/// never crash the host.
/// </summary>
public sealed class ConversationConductor
{
    private readonly IConversationRouter _router;
    private readonly StreamPump _pump;
    private readonly ILogger<ConversationConductor> _logger;

    public ConversationConductor(
        IConversationRouter router,
        StreamPump pump,
        ILogger<ConversationConductor>? logger = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
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
