using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Streaming;
using Scout.Conversation.Telemetry;

namespace Scout.Conversation;

/// <summary>
/// Adapter-facing handler: routes an inbound message to a streaming response
/// and pumps it into the channel-bound sink. Wraps the work in an OTel activity.
/// A sender outside <see cref="AccessOptions"/> gets a fixed refusal reply and
/// nothing else runs.
/// Failures surface through the sink's <see cref="IResponseSink.FailAsync"/> and
/// never crash the host.
/// </summary>
public sealed class ConversationConductor
{
    private const string NotAllowedReply = "Scout only answers its configured users.";
    private const string NoAllowedUsersReply =
        "Scout has no allowed users yet. The owner can add them in the Scout desktop app under Settings.";

    private readonly IConversationRouter _router;
    private readonly StreamPump _pump;
    private readonly ILogger<ConversationConductor> _logger;
    private readonly AccessOptions _access;

    public ConversationConductor(
        IConversationRouter router,
        StreamPump pump,
        ILogger<ConversationConductor>? logger = null,
        IOptions<AccessOptions>? access = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
        _logger = logger ?? NullLogger<ConversationConductor>.Instance;
        _access = access?.Value ?? new AccessOptions();
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
            if (RefusalFor(message.UserId) is { } refusal)
            {
                _logger.LogInformation(
                    "Refused an inbound message for channel {ChannelId}: the sender is not an allowed user.",
                    message.ChannelId);
                await sink.BeginAsync(cancellationToken).ConfigureAwait(false);
                await sink.CompleteAsync(refusal, [], cancellationToken).ConfigureAwait(false);
                return;
            }

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

    private string? RefusalFor(string userId)
    {
        var allowed = _access.AllowedUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToList();
        if (allowed.Count == 0)
        {
            return _access.RequireAllowList ? NoAllowedUsersReply : null;
        }

        var sender = userId.Trim();
        return allowed.Exists(id => string.Equals(id, sender, StringComparison.Ordinal))
            ? null
            : NotAllowedReply;
    }
}
