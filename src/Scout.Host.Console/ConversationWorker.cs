using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Conversation;
using Scout.Conversation.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// Background service that connects the active <see cref="IChatAdapter"/> and
/// routes its inbound messages through the <see cref="ConversationConductor"/>.
/// </summary>
public sealed class ConversationWorker(
    IChatAdapter adapter,
    ConversationConductor conductor,
    ILogger<ConversationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting chat adapter {Adapter}.", adapter.Name);
        await adapter.StartAsync(conductor.HandleAsync, stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await adapter.StopAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
