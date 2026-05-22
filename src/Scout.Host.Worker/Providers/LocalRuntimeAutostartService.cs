using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scout.Host.Worker.Providers;

/// <summary>
/// Hosted service that fires the configured local-runtime autostart actions
/// during host start-up. Runs fire-and-forget so a slow daemon cannot block
/// the web host from coming online.
/// </summary>
public sealed class LocalRuntimeAutostartService : IHostedService
{
    private const string OllamaId = OllamaProviderService.ProviderId;
    private const string LmStudioId = "lm-studio";

    private readonly OllamaProviderService _ollama;
    private readonly ILocalRuntimeConfigStore _configStore;
    private readonly ILogger<LocalRuntimeAutostartService> _logger;

    public LocalRuntimeAutostartService(
        OllamaProviderService ollama,
        ILocalRuntimeConfigStore configStore,
        ILogger<LocalRuntimeAutostartService> logger)
    {
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await EvaluateOllamaAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never fail host start-up because of autostart wiring.
            _logger.LogWarning(ex, "Ollama autostart evaluation failed");
        }

        try
        {
            await EvaluateLmStudioAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LM Studio autostart evaluation failed");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task EvaluateOllamaAsync(CancellationToken ct)
    {
        var enabled = await _configStore.GetAutostartAsync(OllamaId, ct).ConfigureAwait(false);
        if (!enabled)
        {
            _logger.LogDebug("Ollama autostart disabled; skipping");
            return;
        }

        var status = await _ollama.GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.Installed)
        {
            _logger.LogDebug("Ollama autostart skipped: binary not installed");
            return;
        }
        if (status.Running)
        {
            _logger.LogDebug("Ollama autostart skipped: already running");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _ollama.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ollama autostart failed");
            }
        }, ct);
    }

    private async Task EvaluateLmStudioAsync(CancellationToken ct)
    {
        var enabled = await _configStore.GetAutostartAsync(LmStudioId, ct).ConfigureAwait(false);
        if (enabled)
        {
            _logger.LogInformation(
                "lm-studio autostart enabled but lifecycle not implemented; skipping");
        }
    }
}
