using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Host.Worker.Providers;

/// <summary>
/// Composes <see cref="IBinaryLocator"/>, <see cref="IOllamaApiClient"/>,
/// <see cref="IProcessLauncher"/>, <see cref="ILocalRuntimeRegistry"/> and
/// <see cref="ILocalRuntimeConfigStore"/> to expose the live status plus
/// start/restart lifecycle for the local Ollama runtime.
/// </summary>
public sealed class OllamaProviderService
{
    public const string ProviderId = "ollama";
    private const string BinaryName = "ollama";

    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(5);

    private readonly IBinaryLocator _locator;
    private readonly IOllamaApiClient _api;
    private readonly IProcessLauncher _launcher;
    private readonly ILocalRuntimeRegistry _registry;
    private readonly ILocalRuntimeConfigStore _configStore;
    private readonly OllamaProviderOptions _options;
    private readonly ILogger<OllamaProviderService> _logger;

    private readonly SemaphoreSlim _startGate = new(1, 1);

    public OllamaProviderService(
        IBinaryLocator locator,
        IOllamaApiClient api,
        IProcessLauncher launcher,
        ILocalRuntimeRegistry registry,
        ILocalRuntimeConfigStore configStore,
        IOptions<OllamaProviderOptions> options,
        ILogger<OllamaProviderService> logger)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProviderStatus> GetStatusAsync(CancellationToken ct)
    {
        var resolved = await _locator.FindOnPathAsync(BinaryName, ct).ConfigureAwait(false);
        var installed = resolved is not null;

        OllamaTags? tags = null;
        ProviderError? lastError = null;

        if (installed)
        {
            tags = await _api.GetTagsAsync(ct).ConfigureAwait(false);
            if (tags is null)
            {
                lastError = new ProviderError(
                    Code: "providers/unreachable",
                    Message: $"Ollama daemon did not respond at {_options.Endpoint} within {_options.PingTimeout.TotalSeconds:0.#}s");
            }
        }

        var running = tags is not null;
        var models = tags?.Models ?? Array.Empty<ProviderModel>();

        var managed = _registry.GetOwned(ProviderId) is not null;
        var autostart = await _configStore.GetAutostartAsync(ProviderId, ct).ConfigureAwait(false);

        return new ProviderStatus(
            Id: ProviderId,
            Installed: installed,
            Running: running,
            Endpoint: _options.Endpoint,
            Version: null,
            ManagedByWorker: managed,
            Autostart: autostart,
            SupportsLifecycle: installed,
            LastError: lastError,
            Models: models);
    }

    public async Task<ProviderStatus> StartAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.Installed)
        {
            return status;
        }
        if (status.Running)
        {
            return status;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the critical section in case a concurrent caller
            // already brought the daemon up.
            status = await GetStatusAsync(ct).ConfigureAwait(false);
            if (status.Running)
            {
                return status;
            }
            if (!status.Installed)
            {
                return status;
            }

            var binaryPath = await _locator.FindOnPathAsync(BinaryName, ct).ConfigureAwait(false);
            if (binaryPath is null)
            {
                // Lost the race: binary disappeared between status check and now.
                return await GetStatusAsync(ct).ConfigureAwait(false);
            }

            var request = new ProcessLaunchRequest(
                FileName: binaryPath,
                Arguments: new[] { "serve" });

            _logger.LogInformation("Spawning ollama daemon: {Path} serve", binaryPath);
            var proc = _launcher.Launch(request);
            _registry.Track(ProviderId, proc);

            try
            {
                await WaitForReadinessAsync(proc, ct).ConfigureAwait(false);
            }
            catch
            {
                // On any failure we own the process: try to stop and untrack
                // so we don't leak a runaway daemon.
                try
                {
                    await proc.StopAsync(StopGrace, ct).ConfigureAwait(false);
                }
                catch (Exception stopEx)
                {
                    _logger.LogWarning(stopEx, "Failed to stop ollama daemon during start cleanup");
                }
                _registry.Untrack(ProviderId);
                try
                {
                    await proc.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeEx)
                {
                    _logger.LogDebug(disposeEx, "Dispose of failed ollama process raised");
                }
                throw;
            }

            return await GetStatusAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<ProviderStatus> RestartAsync(CancellationToken ct)
    {
        var owned = _registry.GetOwned(ProviderId);
        if (owned is null)
        {
            throw new ProviderNotManagedException(
                ProviderId,
                "Cannot restart externally-started daemon");
        }

        var graceful = await owned.StopAsync(RestartGrace, ct).ConfigureAwait(false);
        if (!graceful)
        {
            _logger.LogWarning(
                "Ollama daemon (pid {Pid}) did not exit within the grace window and was force-killed",
                owned.Pid);
        }

        _registry.Untrack(ProviderId);
        try
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dispose of stopped ollama process raised");
        }

        return await StartAsync(ct).ConfigureAwait(false);
    }

    private async Task WaitForReadinessAsync(ILaunchedProcess proc, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _options.StartTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (proc.HasExited)
            {
                throw new ProviderStartFailedException(
                    ProviderId,
                    "daemon exited before becoming reachable");
            }

            var tags = await _api.GetTagsAsync(ct).ConfigureAwait(false);
            if (tags is not null)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new ProviderStartTimeoutException(
                    ProviderId,
                    $"Ollama daemon did not become reachable at {_options.Endpoint} within {_options.StartTimeout.TotalSeconds:0.#}s");
            }

            try
            {
                await Task.Delay(ReadinessPollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }
}
