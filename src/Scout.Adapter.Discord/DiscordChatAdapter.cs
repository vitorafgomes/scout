using Microsoft.Extensions.Options;
using Scout.Core.Chat;

namespace Scout.Adapter.Discord;

/// <summary>
/// Discord implementation of <see cref="IChatAdapter"/>. This initial
/// scaffold wires validation and the <see cref="IChatAdapter"/> surface only;
/// the Discord.Net client connection and message handling are added in a
/// follow-up PR (PR 3 of Plan 2) to keep this PR focused on CI green and
/// the project baseline.
/// </summary>
public sealed class DiscordChatAdapter : IChatAdapter
{
    private readonly string _botToken;

    public DiscordChatAdapter(IOptions<DiscordOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);

        if (string.IsNullOrWhiteSpace(options.Value.BotToken))
        {
            throw new ArgumentException(
                "DiscordOptions.BotToken must be non-empty. Configure it via " +
                "the SCOUT__ADAPTERS__DISCORD__BOTTOKEN environment variable " +
                "or User Secrets — never commit the token.",
                nameof(options));
        }

        _botToken = options.Value.BotToken;
    }

    public string Name => "Discord";

    public Task StartAsync(
        Func<ChatMessage, CancellationToken, Task<ChatResponse>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // Wiring to Discord.Net arrives in PR 3 of Plan 2. Until then the
        // adapter is a valid no-op that still honors the contract.
        _ = _botToken;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
