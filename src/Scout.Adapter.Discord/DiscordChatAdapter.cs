using System.Collections.Concurrent;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Adapter.Discord.Streaming;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;

namespace Scout.Adapter.Discord;

/// <summary>
/// Discord implementation of the inverted <see cref="IChatAdapter"/>. Owns a
/// single <see cref="DiscordSocketClient"/> that listens for messages
/// addressing the bot, normalizes each into a <see cref="ChatMessage"/>, builds
/// a <see cref="DiscordResponseSink"/> bound to the originating channel (the
/// descriptive-write boundary — replies write only where the message came
/// from), and invokes the handler with both. The conductor drives the sink.
/// With <see cref="DiscordOptions.ReplyInThreads"/>, a mention in a server text
/// channel first opens a public thread on the user's message and the reply goes
/// there; when the thread cannot be created the reply stays in the channel.
/// </summary>
public sealed class DiscordChatAdapter : IChatAdapter, IAsyncDisposable
{
    // ponytail: fixed 10 s ceiling on waiting for in-flight replies at stop; make it configurable if a host needs a longer drain.
    private static readonly TimeSpan InFlightDrainTimeout = TimeSpan.FromSeconds(10);

    private const int ThreadNameLength = 60;
    private const string VoiceThreadName = "Voice message";

    private readonly string _botToken;
    private readonly bool _replyInThreads;
    private readonly AccessPolicy _access;
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
        ILogger<DiscordChatAdapter> logger,
        AccessPolicy access)
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
        _replyInThreads = options.Value.ReplyInThreads;
        _access = access ?? throw new ArgumentNullException(nameof(access));

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
            var repliesToSelf = (message as IUserMessage)?.ReferencedMessage?.Author?.Id == botUserId;
            var inbound = ProjectInbound(
                messageId: message.Id,
                authorId: message.Author.Id,
                channelId: message.Channel.Id,
                content: message.Content,
                mentionsSelf: mentionsSelf,
                timestamp: message.Timestamp,
                guildId: guildId,
                repliesToSelf: repliesToSelf,
                voice: VoiceOf(message));

            return OnInboundAsync(inbound, botUserId, message.Channel, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "DiscordChatAdapter failed while processing message {MessageId}", message.Id);
            return Task.CompletedTask;
        }
    }

    internal Task OnInboundAsync(
        InboundDiscordMessage inbound,
        ulong botUserId,
        IMessageChannel channel,
        IMessage? source = null)
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
            "Discord message {MessageId} accepted from channel {ChannelId} (textLen={Length}, voice={Voice}); dispatching to conversation.",
            inbound.MessageId, inbound.ChannelId, chatMessage!.Text.Length, chatMessage.Audio is not null);

        var id = Interlocked.Increment(ref _nextConversationId);
        var conversation = Task.Run(
            () => RunConversationAsync(handler, chatMessage, inbound, channel, source, lifetimeToken),
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
        InboundDiscordMessage inbound,
        IMessageChannel channel,
        IMessage? source,
        CancellationToken lifetimeToken)
    {
        var messageId = inbound.MessageId;
        try
        {
            var thread = await TryOpenThreadAsync(chatMessage, channel, source, lifetimeToken).ConfigureAwait(false);
            var replyChannel = thread ?? channel;
            var message = thread is null
                ? chatMessage
                : new ChatMessage(
                    chatMessage.UserId, thread.Id.ToString(CultureInfo.InvariantCulture), chatMessage.Text, chatMessage.Timestamp, chatMessage.Audio)
                { OriginReference = DiscordOriginPoster.Reference(thread.Id) };
            var replyTo = thread is null
                ? new MessageReference(messageId, inbound.ChannelId, inbound.GuildId, failIfNotExists: false)
                : null;

            using var sink = new DiscordResponseSink(replyChannel, replyTo, logger: _logger);
            await handler(message, sink, lifetimeToken).ConfigureAwait(false);
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

    // A public thread on the user's message, for a mention in a server text
    // channel from a user the access policy admits; null for a refused user, a
    // DM, a thread, a voice channel's chat, or when Discord refuses (missing
    // permission), in which case the reply stays in the channel.
    private async Task<IThreadChannel?> TryOpenThreadAsync(
        ChatMessage chatMessage,
        IMessageChannel channel,
        IMessage? source,
        CancellationToken cancellationToken)
    {
        if (!_replyInThreads || source is null || !_access.IsAllowed(chatMessage.UserId)
            || channel is not ITextChannel text || channel is IThreadChannel || channel is IVoiceChannel)
        {
            return null;
        }

        try
        {
            return await text.CreateThreadAsync(
                    ThreadName(chatMessage.Text),
                    ThreadType.PublicThread,
                    ThreadArchiveDuration.OneDay,
                    source,
                    options: new RequestOptions { CancelToken = cancellationToken })
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Could not open a thread for Discord message {MessageId}; replying in the channel instead.",
                source.Id);
            return null;
        }
    }

    internal static string ThreadName(string question)
    {
        var name = question.ReplaceLineEndings(" ").Trim();
        if (name.Length == 0)
        {
            return VoiceThreadName;
        }
        if (name.Length <= ThreadNameLength)
        {
            return name;
        }

        var space = name.LastIndexOf(' ', ThreadNameLength);
        return (space > ThreadNameLength / 2 ? name[..space] : name[..ThreadNameLength]).TrimEnd() + "...";
    }

    private static InboundVoiceMessage? VoiceOf(SocketMessage message)
    {
        if (message.Flags is not { } flags || !flags.HasFlag(MessageFlags.VoiceMessage))
        {
            return null;
        }

        var audio = message.Attachments.FirstOrDefault(
            a => a.ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true);
        return audio is null
            ? null
            : new InboundVoiceMessage(
                audio.Url,
                audio.ContentType!,
                audio.Size,
                audio.Duration is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
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
        ulong? guildId,
        bool repliesToSelf = false,
        InboundVoiceMessage? voice = null)
        => new(
            MessageId: messageId,
            AuthorId: authorId,
            ChannelId: channelId,
            Content: content ?? string.Empty,
            MentionsSelf: mentionsSelf,
            Timestamp: timestamp,
            GuildId: guildId,
            RepliesToSelf: repliesToSelf,
            Voice: voice);

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

    internal async Task<IMessageChannel?> FindChannelAsync(ulong channelId, CancellationToken cancellationToken)
    {
        if (!_lifecycle.IsRunning)
        {
            return null;
        }

        return _client.GetChannel(channelId) as IMessageChannel
            ?? await _client.GetChannelAsync(channelId, new RequestOptions { CancelToken = cancellationToken })
                .ConfigureAwait(false) as IMessageChannel;
    }
}
