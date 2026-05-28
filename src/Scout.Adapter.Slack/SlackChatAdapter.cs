using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Adapter.Slack.Streaming;
using Scout.Conversation.Abstractions;
using SlackNet;
using SlackNet.Events;

namespace Scout.Adapter.Slack;

/// <summary>
/// Slack implementation of the inverted <see cref="IChatAdapter"/>. Owns a
/// single Socket Mode connection (no public URL) that receives message events,
/// normalizes each into a <see cref="ChatMessage"/>, builds a
/// <see cref="SlackResponseSink"/> bound to the originating channel and thread
/// (the descriptive-write boundary — replies write only where the message came
/// from), and invokes the handler with both. The conductor drives the sink.
/// </summary>
public sealed class SlackChatAdapter : IChatAdapter, IAsyncDisposable
{
    private readonly SlackMessageTranslator _translator;
    private readonly ILogger<SlackChatAdapter> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ISlackApiClient _apiClient;
    private readonly ISlackSocketModeClient _socketModeClient;
    private readonly SlackAdapterLifecycle _lifecycle = new();

    private Func<ChatMessage, IResponseSink, CancellationToken, Task>? _handler;
    private string _botUserId = string.Empty;

    public SlackChatAdapter(
        IOptions<SlackOptions> options,
        SlackMessageTranslator translator,
        ILogger<SlackChatAdapter> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.BotToken))
        {
            throw new ArgumentException(
                "SlackOptions.BotToken must be non-empty. Configure it via the " +
                "SCOUT__ADAPTERS__SLACK__BOTTOKEN environment variable or User " +
                "Secrets — never commit the token.",
                nameof(options));
        }
        if (string.IsNullOrWhiteSpace(value.AppLevelToken))
        {
            throw new ArgumentException(
                "SlackOptions.AppLevelToken must be non-empty. Configure it via the " +
                "SCOUT__ADAPTERS__SLACK__APPLEVELTOKEN environment variable or User " +
                "Secrets — never commit the token.",
                nameof(options));
        }

        // Build a self-contained SlackNet service graph from the two tokens and
        // route every inbound message event through this adapter. Socket Mode
        // needs both the bot token (Web API) and the app-level token (gateway).
        var services = new SlackServiceBuilder()
            .UseApiToken(value.BotToken)
            .UseAppLevelToken(value.AppLevelToken)
            .RegisterEventHandler(_ => new MessageEventForwarder(this));

        _apiClient = services.GetApiClient();
        _socketModeClient = services.GetSocketModeClient();
    }

    /// <summary>Internal constructor for tests: inject the SlackNet seams directly.</summary>
    internal SlackChatAdapter(
        ISlackApiClient apiClient,
        ISlackSocketModeClient socketModeClient,
        SlackMessageTranslator translator,
        ILogger<SlackChatAdapter> logger,
        string botUserId,
        TimeProvider? timeProvider = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _socketModeClient = socketModeClient ?? throw new ArgumentNullException(nameof(socketModeClient));
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _botUserId = botUserId ?? string.Empty;
    }

    public string Name => "Slack";

    public async Task StartAsync(
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Throws if already started or stopped (single-connect) and arms the
        // lifetime token linked to the host's start token.
        _lifecycle.Start(cancellationToken);
        _handler = handler;

        // Resolve the bot's own user id so the translator can drop self-authored
        // messages and strip the bot's mention.
        var auth = await _apiClient.Auth.Test(cancellationToken).ConfigureAwait(false);
        _botUserId = auth.UserId ?? string.Empty;
        _logger.LogInformation(
            "Slack bot ready: user={BotUserId} team={Team}", _botUserId, auth.Team ?? string.Empty);

        await _socketModeClient.Connect(new SlackNet.SocketMode.SocketModeConnectionOptions(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancels the lifetime token; returns false if not currently running.
        if (!await _lifecycle.StopAsync().ConfigureAwait(false))
        {
            return;
        }

        _handler = null;
        _socketModeClient.Disconnect();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Dispose();
        (_socketModeClient as IDisposable)?.Dispose();
    }

    // Entry point from the SlackNet event handler. Internal so tests can drive a
    // message through the full adapter path without a live socket.
    internal async Task OnMessageReceivedAsync(MessageEvent message)
    {
        var handler = _handler;
        if (handler is null || !_lifecycle.IsRunning || message is null)
        {
            return;
        }
        var lifetimeToken = _lifecycle.LifetimeToken;

        try
        {
            var inbound = ProjectInbound(
                userId: message.User,
                channelId: message.Channel,
                text: message.Text,
                timestamp: message.Timestamp,
                messageTs: message.Ts,
                threadTs: message.ThreadTs,
                channelType: message.ChannelType,
                subtype: message.Subtype,
                botId: message.BotId,
                appId: message.AppId);

            if (!_translator.TryTranslate(inbound, _botUserId, out var chatMessage, out var reason))
            {
                _logger.LogDebug(
                    "Slack message {Ts} ignored: {Reason}", inbound.MessageTs, reason);
                return;
            }

            // Reply in the originating thread: use the parent thread when the
            // message is already threaded, otherwise start a thread on it. Either
            // way the sink is bound to the originating channel only.
            var threadTs = string.IsNullOrEmpty(inbound.ThreadTs)
                ? inbound.MessageTs
                : inbound.ThreadTs;
            var sink = new SlackResponseSink(
                _apiClient, inbound.ChannelId, threadTs, _timeProvider, _logger);

            await handler(chatMessage!, sink, lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            _logger.LogDebug("Slack message {Ts} cancelled on shutdown.", message.Ts);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "SlackChatAdapter failed while processing message {Ts}", message.Ts);
        }
    }

    // Pure projection of the Slack event fields into the translator DTO.
    // Extracted so the inbound glue is unit-testable without SlackNet's
    // MessageEvent.
    internal static InboundSlackMessage ProjectInbound(
        string? userId,
        string? channelId,
        string? text,
        DateTimeOffset timestamp,
        string? messageTs,
        string? threadTs,
        string? channelType,
        string? subtype,
        string? botId,
        string? appId)
        => new(
            UserId: userId ?? string.Empty,
            ChannelId: channelId ?? string.Empty,
            Text: text ?? string.Empty,
            Timestamp: timestamp,
            MessageTs: messageTs ?? string.Empty,
            ThreadTs: threadTs,
            ChannelType: channelType,
            Subtype: subtype,
            BotId: botId,
            AppId: appId);

    // Adapts SlackNet's per-event handler contract to the adapter. SlackNet
    // resolves one handler per event; this thin shim forwards to the long-lived
    // adapter instance.
    private sealed class MessageEventForwarder(SlackChatAdapter adapter) : IEventHandler<MessageEvent>
    {
        public Task Handle(MessageEvent slackEvent) => adapter.OnMessageReceivedAsync(slackEvent);
    }
}
