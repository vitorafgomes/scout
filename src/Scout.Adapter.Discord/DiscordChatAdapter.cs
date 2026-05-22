using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Adapter.Discord.Streaming;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord;

/// <summary>
/// Discord implementation of <see cref="IChatAdapter"/>. Owns a single
/// <see cref="DiscordSocketClient"/> that listens for guild messages that
/// mention the bot, maps each one into an <see cref="InboundDiscordMessage"/>,
/// and delegates to <see cref="DiscordMessageHandler"/> for translation +
/// dispatch. The resulting <see cref="ChatResponse"/> (plain or streaming)
/// is handed to <see cref="IDiscordReplyPoster"/>, which owns posting and
/// incremental edits. The read-only invariant allows a reply to the inbound
/// message but no other writes.
/// </summary>
public sealed class DiscordChatAdapter : IChatAdapter, IAsyncDisposable
{
    private readonly string _botToken;
    private readonly DiscordMessageHandler _messageHandler;
    private readonly IDiscordReplyPoster _poster;
    private readonly ILogger<DiscordChatAdapter> _logger;
    private readonly DiscordSocketClient _client;

    private Func<ChatMessage, CancellationToken, Task<ChatResponse>>? _scoutHandler;
    private int _started;

    public DiscordChatAdapter(
        IOptions<DiscordOptions> options,
        DiscordMessageHandler messageHandler,
        IDiscordReplyPoster poster,
        ILogger<DiscordChatAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
        _poster = poster ?? throw new ArgumentNullException(nameof(poster));
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
            // Minimum intents Scout needs: listen to messages in guild
            // channels and read their content. No DMs, no reactions,
            // no presence. The intent surface stays as narrow as possible.
            GatewayIntents = GatewayIntents.Guilds
                           | GatewayIntents.GuildMessages
                           | GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info,
        });
    }

    public string Name => "Discord";

    public async Task StartAsync(
        Func<ChatMessage, CancellationToken, Task<ChatResponse>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException(
                $"{nameof(DiscordChatAdapter)} has already been started.");
        }

        _scoutHandler = handler;

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
        if (Interlocked.CompareExchange(ref _started, 0, 1) == 0)
        {
            return;
        }

        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.Ready -= OnReadyAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.Connected -= OnConnectedAsync;
        _client.Log -= OnDiscordLogAsync;
        _scoutHandler = null;

        await _client.StopAsync().ConfigureAwait(false);
        await _client.LogoutAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        var handler = _scoutHandler;
        if (handler is null)
        {
            return;
        }

        try
        {
            var botUserId = _client.CurrentUser?.Id ?? 0UL;
            if (botUserId == 0)
            {
                return;
            }

            var guildId = (message.Channel as SocketGuildChannel)?.Guild.Id;
            var inbound = new InboundDiscordMessage(
                MessageId: message.Id,
                AuthorId: message.Author.Id,
                ChannelId: message.Channel.Id,
                Content: message.Content ?? string.Empty,
                MentionsSelf: message.MentionedUsers.Any(u => u.Id == botUserId),
                Timestamp: message.Timestamp,
                GuildId: guildId);

            var response = await _messageHandler
                .HandleAsync(inbound, botUserId, handler, CancellationToken.None)
                .ConfigureAwait(false);

            if (response is null)
            {
                return;
            }

            await _poster
                .PostAsync(message.Channel, response, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "DiscordChatAdapter failed while processing message {MessageId}",
                message.Id);
        }
    }

    private Task OnReadyAsync()
    {
        var user = _client.CurrentUser;
        var guildCount = _client.Guilds?.Count ?? 0;
        _logger.LogInformation(
            "bot ready: user={BotUsername} id={BotUserId} guilds={GuildCount}",
            user?.Username ?? string.Empty,
            user?.Id ?? 0UL,
            guildCount);
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
