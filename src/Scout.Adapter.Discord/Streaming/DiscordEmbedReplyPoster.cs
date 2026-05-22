using System.Text;
using Discord;
using Microsoft.Extensions.Logging;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord.Streaming;

/// <summary>
/// Default <see cref="IDiscordReplyPoster"/>. Plain responses go out as a
/// single SendMessageAsync. Streaming responses post an initial embed and
/// edit it via <see cref="EmbedEditThrottler"/> as chunks arrive.
/// </summary>
public sealed class DiscordEmbedReplyPoster : IDiscordReplyPoster
{
    // Streaming embed limits. Centralized so tests can assert against them.
    internal const int MaxEmbedDescriptionLength = 4096;
    internal const string CursorGlyph = "▌"; // ▌
    internal const string EmptyStreamPlaceholder = "(no response from model)";
    internal const string ConnectionErrorFooter = "\n\n(connection error)";
    internal static readonly TimeSpan DefaultEditInterval = TimeSpan.FromMilliseconds(750);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiscordEmbedReplyPoster> _logger;
    private readonly DiscordMessageTranslator _translator;

    public DiscordEmbedReplyPoster(
        TimeProvider timeProvider,
        ILogger<DiscordEmbedReplyPoster> logger)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _translator = new DiscordMessageTranslator();
    }

    public Task PostAsync(
        IMessageChannel channel,
        ChatResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(response);

        return response.IsStreaming
            ? PostStreamingAsync(channel, response, cancellationToken)
            : PostPlainAsync(channel, response, cancellationToken);
    }

    private async Task PostPlainAsync(
        IMessageChannel channel,
        ChatResponse response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rendered = _translator.Render(response);
        await channel.SendMessageAsync(rendered).ConfigureAwait(false);
    }

    private async Task PostStreamingAsync(
        IMessageChannel channel,
        ChatResponse response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Best-effort typing indicator; any transient failure must not break
        // the reply path.
        try
        {
            _ = channel.TriggerTypingAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TriggerTypingAsync failed; continuing with reply");
        }

        // Discord rejects embeds with empty descriptions, so seed with the
        // cursor character.
        var initialEmbed = new EmbedBuilder()
            .WithDescription(CursorGlyph)
            .Build();

        var posted = await channel
            .SendMessageAsync(embed: initialEmbed)
            .ConfigureAwait(false);

        var options = new EmbedEditThrottlerOptions
        {
            EditAsync = async (body, _) =>
            {
                await posted.ModifyAsync(props =>
                {
                    props.Embed = new EmbedBuilder().WithDescription(body).Build();
                }).ConfigureAwait(false);
            },
            EditInterval = DefaultEditInterval,
            MaxLength = MaxEmbedDescriptionLength,
            Cursor = CursorGlyph,
            EmptyStreamPlaceholder = EmptyStreamPlaceholder,
            TimeProvider = _timeProvider,
            Logger = _logger,
        };

        await using var throttler = new EmbedEditThrottler(options);

        try
        {
            await foreach (var chunk in response.Stream!
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                await throttler.AppendAsync(chunk, cancellationToken).ConfigureAwait(false);
            }

            if (response.Citations.Count > 0)
            {
                var footer = BuildCitationFooter(response.Citations);
                await throttler.AppendAsync(footer, cancellationToken).ConfigureAwait(false);
            }

            await throttler.FinalizeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Streaming response failed mid-flight; finalizing with partial content");
            await throttler
                .FinalizeWithErrorAsync(ConnectionErrorFooter, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    // Mirrors DiscordMessageTranslator.Render: blank line, "Sources:", then
    // "- <citation>" per line. Uses Environment.NewLine + TrimEnd so the
    // streaming footer is byte-for-byte identical to the plain-mode footer.
    internal static string BuildCitationFooter(IReadOnlyList<Citation> citations)
    {
        var nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append(nl);
        sb.Append(nl);
        sb.Append("Sources:").Append(nl);
        foreach (var citation in citations)
        {
            sb.Append("- ").Append(citation.ToString()).Append(nl);
        }
        return sb.ToString().TrimEnd();
    }
}
