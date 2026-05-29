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
    private readonly string _botToken;
    private readonly DiscordMessageTranslator _translator;
    private readonly ILogger<DiscordChatAdapter> _logger;
    private readonly DiscordSocketClient _client;
    private readonly DiscordAdapterLifecycle _lifecycle = new();

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

        await _client.LoginAsync(TokenType.Bot, _botToken).ConfigureAwait(false);
        await _client.StartAsync().ConfigureAwait(false);
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

        await _client.StopAsync().ConfigureAwait(false);
        await _client.LogoutAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        var handler = _handler;
        if (handler is null || !_lifecycle.IsRunning)
        {
            return;
        }
        var lifetimeToken = _lifecycle.LifetimeToken;

        try
        {
            var botUserId = _client.CurrentUser?.Id ?? 0UL;
            if (botUserId == 0)
            {
                return;
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

            if (!_translator.TryTranslate(inbound, botUserId, out var chatMessage, out var reason))
            {
                _logger.LogDebug(
                    "Discord message {MessageId} ignored: {Reason}", message.Id, reason);
                return;
            }

            _logger.LogInformation(
                "Discord message {MessageId} accepted from channel {ChannelId} (textLen={Length}); dispatching to conversation.",
                message.Id, message.Channel.Id, chatMessage!.Text.Length);

            // Bind the sink to the originating channel only and pass the
            // lifetime token so the conversation cancels on host shutdown.
            var sink = new DiscordResponseSink(message.Channel, logger: _logger);
            await handler(chatMessage!, sink, lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Discord message {MessageId} cancelled on shutdown.", message.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "DiscordChatAdapter failed while processing message {MessageId}", message.Id);
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
