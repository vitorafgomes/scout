using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Conversation;
using Scout.Conversation.Abstractions;

namespace Scout.Host.Console;

/// <summary>
/// Background service that connects every configured <see cref="IChatAdapter"/>
/// and routes their inbound messages through the <see cref="ConversationConductor"/>.
/// When no adapter is configured it logs actionable guidance and stops the host
/// gracefully instead of throwing.
/// </summary>
public sealed class ConversationWorker(
    IEnumerable<IChatAdapter> adapters,
    ConversationConductor conductor,
    IHostApplicationLifetime lifetime,
    ILogger<ConversationWorker> logger) : BackgroundService
{
    private readonly IReadOnlyList<IChatAdapter> _adapters = adapters.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_adapters.Count == 0)
        {
            logger.LogError(
                "No chat adapter is configured. Set SCOUT__ADAPTERS__DISCORD__BOTTOKEN " +
                "(or the Slack/Teams equivalents) and restart.");
            lifetime.StopApplication();
            return;
        }

        foreach (var adapter in _adapters)
        {
            logger.LogInformation("Starting chat adapter {Adapter}.", adapter.Name);
            await adapter.StartAsync(conductor.HandleAsync, stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var adapter in _adapters)
        {
            await adapter.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
