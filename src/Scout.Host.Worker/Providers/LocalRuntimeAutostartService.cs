using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Host.Worker.Providers;

/// <summary>
/// Hosted lifecycle service that warms up the local Ollama runtime during
/// host start-up and gates consumers via <see cref="OllamaReadinessProbe"/>.
/// Runs inside the <c>StartingAsync</c> phase so the rest of the host can
/// rely on the probe being resolved before any HTTP request lands.
/// </summary>
public sealed class LocalRuntimeAutostartService : IHostedLifecycleService
{
    private const string OllamaId = OllamaProviderService.ProviderId;
    private const string LmStudioId = "lm-studio";

    private readonly OllamaProviderService _ollama;
    private readonly ILocalRuntimeConfigStore _configStore;
    private readonly OllamaReadinessProbe _probe;
    private readonly OllamaProviderOptions _options;
    private readonly ILogger<LocalRuntimeAutostartService> _logger;

    public LocalRuntimeAutostartService(
        OllamaProviderService ollama,
        ILocalRuntimeConfigStore configStore,
        OllamaReadinessProbe probe,
        IOptions<OllamaProviderOptions> options,
        ILogger<LocalRuntimeAutostartService> logger)
    {
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _probe.MarkStarting();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_options.WarmupBudget);

        try
        {
            await EvaluateOllamaAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Ollama warmup budget exhausted; marking probe failed");
            _probe.MarkFailed();
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama autostart evaluation failed");
            _probe.MarkFailed();
            return;
        }

        try
        {
            await EvaluateLmStudioAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LM Studio autostart evaluation failed");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EvaluateOllamaAsync(CancellationToken ct)
    {
        var status = await _ollama
            .GetStatusInternalAsync(ct, awaitReadiness: false)
            .ConfigureAwait(false);

        if (!status.Installed)
        {
            _logger.LogDebug("Ollama autostart skipped: binary not installed");
            _probe.MarkFailed();
            return;
        }
        if (status.Running)
        {
            _logger.LogDebug("Ollama autostart skipped: already running");
            _probe.MarkReady();
            return;
        }

        var enabled = await _configStore.GetAutostartAsync(OllamaId, ct).ConfigureAwait(false);
        if (!enabled)
        {
            _logger.LogDebug("Ollama autostart disabled; skipping");
            _probe.MarkFailed();
            return;
        }

        try
        {
            await _ollama.StartInternalAsync(ct, skipReadinessCheck: true).ConfigureAwait(false);
            _probe.MarkReady();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama autostart failed");
            _probe.MarkFailed();
        }
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
