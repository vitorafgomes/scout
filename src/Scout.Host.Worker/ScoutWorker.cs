using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Core.Chat;
using Scout.Core.Hosting;

namespace Scout.Host.Worker;

/// <summary>
/// Background service that starts the configured <see cref="IChatAdapter"/>
/// wiring it to <see cref="ScoutHost.HandleAsync"/>. On shutdown the adapter
/// is stopped cooperatively.
///
/// The actual chat plumbing (Discord / Slack / Teams) lives in the adapter
/// project; this worker only glues the adapter's lifetime to the .NET host.
/// </summary>
public sealed class ScoutWorker : BackgroundService
{
    private readonly IChatAdapter _adapter;
    private readonly ScoutHost _scoutHost;
    private readonly ILogger<ScoutWorker> _logger;

    public ScoutWorker(
        IChatAdapter adapter,
        ScoutHost scoutHost,
        ILogger<ScoutWorker> logger)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _scoutHost = scoutHost ?? throw new ArgumentNullException(nameof(scoutHost));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Scout with chat adapter '{Adapter}'", _adapter.Name);

        try
        {
            await _adapter.StartAsync(_scoutHost.HandleAsync, stoppingToken).ConfigureAwait(false);

            // Signal operational readiness to the Electron supervisor after the
            // chat adapter has connected to the gateway. The supervisor waits for
            // this line on stdout before transitioning the worker state to 'running'.
            Console.WriteLine("SCOUT_READY");
            Console.Out.Flush();

            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cooperative shutdown — normal.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Scout adapter '{Adapter}' failed; the host will stop.",
                _adapter.Name);
            throw;
        }
        finally
        {
            try
            {
                await _adapter.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chat adapter failed to stop cleanly.");
            }
        }
    }
}
