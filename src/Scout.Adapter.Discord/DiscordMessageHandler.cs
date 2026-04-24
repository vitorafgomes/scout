using Microsoft.Extensions.Logging;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord;

/// <summary>
/// Bridges an inbound Discord payload and a Scout core handler. Isolated
/// from <see cref="DiscordChatAdapter"/> so the flow (translate -> invoke
/// handler -> render) can be unit-tested without touching Discord.Net.
/// </summary>
public sealed class DiscordMessageHandler
{
    private const string GenericErrorReply =
        "Sorry, something went wrong while answering. Please try again.";

    private readonly DiscordMessageTranslator _translator;
    private readonly ILogger<DiscordMessageHandler> _logger;

    public DiscordMessageHandler(
        DiscordMessageTranslator translator,
        ILogger<DiscordMessageHandler> logger)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Process one inbound Discord message. Returns the text to post back to
    /// Discord, or <c>null</c> if the translator decided the message should
    /// be ignored (bot author, no mention, empty content after stripping).
    /// </summary>
    public async Task<string?> HandleAsync(
        InboundDiscordMessage inbound,
        ulong botUserId,
        Func<ChatMessage, CancellationToken, Task<ChatResponse>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(handler);

        // Inbound trail: emit before translation so the log captures every
        // message that reaches the adapter, including ones that get skipped
        // below. Content itself is not logged - only its length - to keep
        // PII out of operational logs.
        _logger.LogInformation(
            "inbound: guild={GuildId} channel={ChannelId} author={AuthorId} len={ContentLen}",
            inbound.GuildId,
            inbound.ChannelId,
            inbound.AuthorId,
            inbound.Content?.Length ?? 0);

        if (!_translator.TryTranslate(inbound, botUserId, out var chatMessage, out var reason))
        {
            _logger.LogInformation("skipped: reason={Reason}", reason);
            return null;
        }

        var dispatched = LogHelpers.Truncate(chatMessage!.Text, out var dispatchedTruncated);
        _logger.LogInformation(
            "dispatched: content=\"{Content}\" truncated={Truncated}",
            dispatched,
            dispatchedTruncated);

        try
        {
            var response = await handler(chatMessage!, cancellationToken).ConfigureAwait(false);
            var rendered = _translator.Render(response);
            _logger.LogInformation(
                "reply: len={ReplyLength}",
                rendered?.Length ?? 0);
            return rendered;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Handler threw while processing Discord message {MessageId} from user {AuthorId}",
                inbound.MessageId,
                inbound.AuthorId);
            return GenericErrorReply;
        }
    }
}
