using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Adapter.Discord.Streaming;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord;

/// <summary>
/// Discord implementation of the inverted <see cref="IChatAdapter"/>. Owns a
/// single <see cref="DiscordSocketClient"/> that listens for messages
/// addressing the bot, normalizes each into a <see cref="ChatMessage"/>, builds
/// a <see cref="DiscordResponseSink"/> bound to the originating channel (the
/// descriptive-write boundary — replies write only where the message came
/// from), and invokes the handler with both. The conductor drives the sink.
/// </summary>
public sealed class DiscordChatAdapter : IChatAdapter, IAsyncDisposable
{
    // ponytail: fixed 10 s ceiling on waiting for in-flight replies at stop; make it configurable if a host needs a longer drain.
    private static readonly TimeSpan InFlightDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly string _botToken;
    private readonly DiscordMessageTranslator _translator;
    private readonly ILogger<DiscordChatAdapter> _logger;
    private readonly DiscordSocketClient _client;
    private readonly DiscordAdapterLifecycle _lifecycle = new();
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();
    private long _nextConversationId;

    private Func<ChatMessage, IResponseSink, CancellationToken, Task>? _handler;

    public DiscordChatAdapter(
        IOptions<DiscordOptions> options,
        DiscordMessageTranslator translator,
        ILogger<DiscordChatAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(options.Value.BotToken))
        {
            throw new ArgumentException(
                "DiscordOptions.BotToken must be non-empty. Configure it via " +
                "the SCOUT__ADAPTERS__DISCORD__BOTTOKEN environment variable " +
                "or User Secrets — never commit the token.",
                nameof(options));
        }

        _botToken = options.Value.BotToken;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
                           | GatewayIntents.GuildMessages
                           | GatewayIntents.DirectMessages
                           | GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info,
        });
    }

    public string Name => "Discord";

    public async Task StartAsync(
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        Arm(handler, cancellationToken);

        await _client.LoginAsync(TokenType.Bot, _botToken).ConfigureAwait(false);
        await _client.StartAsync().ConfigureAwait(false);
    }

    internal void Arm(
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Throws if already started or stopped (single-login) and arms the
        // lifetime token linked to the host's start token.
        _lifecycle.Start(cancellationToken);
        _handler = handler;

        _client.Log += OnDiscordLogAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.Ready += OnReadyAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.Connected += OnConnectedAsync;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancels the lifetime token; returns false if not currently running.
        if (!await _lifecycle.StopAsync().ConfigureAwait(false))
        {
            return;
        }

        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.Ready -= OnReadyAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.Connected -= OnConnectedAsync;
        _client.Log -= OnDiscordLogAsync;
        _handler = null;

        try
        {
            await Task.WhenAll(_inFlight.Values)
                .WaitAsync(InFlightDrainTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(
                "Discord adapter stopping with {Count} conversation(s) still running.", _inFlight.Count);
        }

        await _client.StopAsync().ConfigureAwait(false);
        await _client.LogoutAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        try
        {
            var botUserId = _client.CurrentUser?.Id ?? 0UL;
            if (botUserId == 0)
            {
                return Task.CompletedTask;
            }

            var guildId = (message.Channel as SocketGuildChannel)?.Guild.Id;
            var mentionsSelf = message.MentionedUsers.Any(u => u.Id == botUserId);
            var inbound = ProjectInbound(
                messageId: message.Id,
                authorId: message.Author.Id,
                channelId: message.Channel.Id,
                content: message.Content,
                mentionsSelf: mentionsSelf,
                timestamp: message.Timestamp,
                guildId: guildId);

            return OnInboundAsync(inbound, botUserId, message.Channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "DiscordChatAdapter failed while processing message {MessageId}", message.Id);
            return Task.CompletedTask;
        }
    }

    internal Task OnInboundAsync(InboundDiscordMessage inbound, ulong botUserId, IMessageChannel channel)
    {
        var handler = _handler;
        if (handler is null || !_lifecycle.IsRunning)
        {
            return Task.CompletedTask;
        }
        var lifetimeToken = _lifecycle.LifetimeToken;

        if (!_translator.TryTranslate(inbound, botUserId, out var chatMessage, out var reason))
        {
            _logger.LogInformation(
                "Discord message {MessageId} ignored: {Reason} (in a server channel the bot only replies when @-mentioned; DMs always work).",
                inbound.MessageId, reason);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Discord message {MessageId} accepted from channel {ChannelId} (textLen={Length}); dispatching to conversation.",
            inbound.MessageId, inbound.ChannelId, chatMessage!.Text.Length);

        var sink = new DiscordResponseSink(channel, logger: _logger);
        var id = Interlocked.Increment(ref _nextConversationId);
        var conversation = Task.Run(
            () => RunConversationAsync(handler, chatMessage, sink, inbound.MessageId, lifetimeToken),
            CancellationToken.None);
        _inFlight[id] = conversation;
        _ = conversation.ContinueWith(
            _ => _inFlight.TryRemove(id, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return Task.CompletedTask;
    }

    private async Task RunConversationAsync(
        Func<ChatMessage, IResponseSink, CancellationToken, Task> handler,
        ChatMessage chatMessage,
        DiscordResponseSink sink,
        ulong messageId,
        CancellationToken lifetimeToken)
    {
        try
        {
            await handler(chatMessage, sink, lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Discord message {MessageId} cancelled on shutdown.", messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "DiscordChatAdapter failed while processing message {MessageId}", messageId);
        }
    }

    // Pure projection of the Discord message fields into the translator DTO.
    // Extracted so the inbound glue is unit-testable without Discord.Net's
    // sealed SocketMessage.
    internal static InboundDiscordMessage ProjectInbound(
        ulong messageId,
        ulong authorId,
        ulong channelId,
        string? content,
        bool mentionsSelf,
        DateTimeOffset timestamp,
        ulong? guildId)
        => new(
            MessageId: messageId,
            AuthorId: authorId,
            ChannelId: channelId,
            Content: content ?? string.Empty,
            MentionsSelf: mentionsSelf,
            Timestamp: timestamp,
            GuildId: guildId);

    private Task OnReadyAsync()
    {
        var user = _client.CurrentUser;
        var guildCount = _client.Guilds?.Count ?? 0;
        _logger.LogInformation(
            "bot ready: user={BotUsername} id={BotUserId} guilds={GuildCount}",
            user?.Username ?? string.Empty, user?.Id ?? 0UL, guildCount);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception? exception)
    {
        var reason = exception?.Message ?? "unknown";
        if (exception is not null)
        {
            _logger.LogWarning(exception, "gateway disconnected: reason={Reason}", reason);
        }
        else
        {
            _logger.LogInformation("gateway disconnected: reason={Reason}", reason);
        }
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync()
    {
        _logger.LogInformation("gateway reconnected");
        return Task.CompletedTask;
    }

    private Task OnDiscordLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information,
        };
        _logger.Log(level, message.Exception, "[Discord] {Source}: {Message}",
            message.Source, message.Message ?? string.Empty);
        return Task.CompletedTask;
    }
}
