using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.ControlPlane.Providers;

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
    private static readonly TimeSpan ExternalStopGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExternalStopMaxWait = TimeSpan.FromSeconds(10);

    private readonly IBinaryLocator _locator;
    private readonly IOllamaApiClient _api;
    private readonly IProcessLauncher _launcher;
    private readonly ILocalRuntimeRegistry _registry;
    private readonly ILocalRuntimeConfigStore _configStore;
    private readonly OllamaProviderOptions _options;
    private readonly IOllamaReadinessProbe _probe;
    private readonly ILogger<OllamaProviderService> _logger;

    private readonly SemaphoreSlim _startGate = new(1, 1);

    public OllamaProviderService(
        IBinaryLocator locator,
        IOllamaApiClient api,
        IProcessLauncher launcher,
        ILocalRuntimeRegistry registry,
        ILocalRuntimeConfigStore configStore,
        IOptions<OllamaProviderOptions> options,
        IOllamaReadinessProbe probe,
        ILogger<OllamaProviderService> logger)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<ProviderStatus> GetStatusAsync(CancellationToken ct) =>
        GetStatusInternalAsync(ct, awaitReadiness: true);

    internal async Task<ProviderStatus> GetStatusInternalAsync(CancellationToken ct, bool awaitReadiness)
    {
        var managed = _registry.GetOwned(ProviderId) is not null;
        var autostart = await _configStore.GetAutostartAsync(ProviderId, ct).ConfigureAwait(false);

        if (awaitReadiness)
        {
            var ready = await _probe.WaitReadyAsync(_options.PingTimeout, ct).ConfigureAwait(false);
            if (!ready)
            {
                return new ProviderStatus(
                    Id: ProviderId,
                    Installed: false,
                    Running: false,
                    Endpoint: _options.Endpoint,
                    Version: null,
                    ManagedByWorker: managed,
                    Autostart: autostart,
                    SupportsLifecycle: false,
                    LastError: new ProviderError(
                        Code: "providers/unreachable",
                        Message: "Local runtime did not become ready"),
                    Models: Array.Empty<ProviderModel>());
            }
        }

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
        // User-initiated start: rearm the probe so that a previous Failed
        // (from autostart) does not lock subsequent calls into a 503.
        // Idempotent against autostart's own MarkStarting via the CAS loop.
        _probe.MarkStarting();

        try
        {
            var status = await StartInternalAsync(ct, skipReadinessCheck: true).ConfigureAwait(false);
            _probe.MarkReady();
            return status;
        }
        catch
        {
            _probe.MarkFailed();
            throw;
        }
    }

    internal async Task<ProviderStatus> StartInternalAsync(CancellationToken ct, bool skipReadinessCheck)
    {
        if (!skipReadinessCheck)
        {
            var ready = await _probe.WaitReadyAsync(_options.PingTimeout, ct).ConfigureAwait(false);
            if (!ready)
            {
                throw new ProviderNotReadyException(
                    ProviderId,
                    "Local runtime is still warming up");
            }
        }

        return await StartCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task<ProviderStatus> StartCoreAsync(CancellationToken ct)
    {
        var status = await GetStatusInternalAsync(ct, awaitReadiness: false).ConfigureAwait(false);
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
            status = await GetStatusInternalAsync(ct, awaitReadiness: false).ConfigureAwait(false);
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
                return await GetStatusInternalAsync(ct, awaitReadiness: false).ConfigureAwait(false);
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

            return await GetStatusInternalAsync(ct, awaitReadiness: false).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<ProviderStatus> RestartAsync(CancellationToken ct)
    {
        await EnsureReadyOrThrowAsync(ct).ConfigureAwait(false);
        await StopCoreAsync(ct).ConfigureAwait(false);
        return await StartCoreAsync(ct).ConfigureAwait(false);
    }

    public async Task<ProviderStatus> StopAsync(CancellationToken ct)
    {
        await EnsureReadyOrThrowAsync(ct).ConfigureAwait(false);
        return await StopCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task<ProviderStatus> StopCoreAsync(CancellationToken ct)
    {
        var status = await GetStatusInternalAsync(ct, awaitReadiness: false).ConfigureAwait(false);
        if (!status.Running)
        {
            return status;
        }

        var owned = _registry.GetOwned(ProviderId);
        if (owned is not null)
        {
            var graceful = await owned.StopAsync(RestartGrace, ct).ConfigureAwait(false);
            if (!graceful)
            {
                _logger.LogWarning(
                    "Ollama daemon (pid {Pid}) did not exit within grace window and was force-killed",
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
        }
        else
        {
            // Externally-started daemon: fall back to OS-level kill. We do not
            // own the PID so we shoot by binary name.
            var killRequest = OperatingSystem.IsWindows()
                ? new ProcessLaunchRequest("taskkill", new[] { "/F", "/IM", "ollama.exe" })
                : new ProcessLaunchRequest("pkill", new[] { "-x", "ollama" });

            _logger.LogInformation(
                "Stopping externally-started ollama daemon via {Tool}",
                killRequest.FileName);

            ILaunchedProcess killer;
            try
            {
                killer = _launcher.Launch(killRequest);
            }
            catch (Exception ex)
            {
                throw new ProviderStopFailedException(
                    ProviderId,
                    $"Failed to invoke {killRequest.FileName} to stop ollama: {ex.Message}");
            }

            try
            {
                using var killCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                killCts.CancelAfter(ExternalStopGrace);
                try
                {
                    await killer.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(
                        "External kill helper did not exit within {Grace}; continuing to poll",
                        ExternalStopGrace);
                }
            }
            finally
            {
                try
                {
                    await killer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Dispose of kill helper raised");
                }
            }
        }

        // Poll until the daemon stops answering /api/tags. If a managed proc
        // already returned from StopAsync the daemon is normally gone; for the
        // external path this is the real boundary check.
        var deadline = DateTimeOffset.UtcNow + ExternalStopMaxWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var tags = await _api.GetTagsAsync(ct).ConfigureAwait(false);
            if (tags is null)
            {
                return await GetStatusInternalAsync(ct, awaitReadiness: false).ConfigureAwait(false);
            }
            await Task.Delay(ReadinessPollInterval, ct).ConfigureAwait(false);
        }

        throw new ProviderStopFailedException(
            ProviderId,
            $"Ollama daemon still reachable at {_options.Endpoint} after {ExternalStopMaxWait.TotalSeconds:0.#}s");
    }

    public async IAsyncEnumerable<PullProgress> PullModelAsync(
        string modelName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        await EnsureReadyOrThrowAsync(ct).ConfigureAwait(false);
        await foreach (var ev in _api.PullAsync(modelName, ct).ConfigureAwait(false))
        {
            yield return ev;
        }
    }

    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken ct)
    {
        await EnsureReadyOrThrowAsync(ct).ConfigureAwait(false);
        var tags = await _api.GetTagsAsync(ct).ConfigureAwait(false);
        return tags?.Models ?? Array.Empty<ProviderModel>();
    }

    public async Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        await EnsureReadyOrThrowAsync(ct).ConfigureAwait(false);
        await _api.DeleteModelAsync(modelName, ct).ConfigureAwait(false);
    }

    public Task SetAutostartAsync(bool enabled, CancellationToken ct) =>
        _configStore.SetAutostartAsync(ProviderId, enabled, ct);

    private async Task EnsureReadyOrThrowAsync(CancellationToken ct)
    {
        var ready = await _probe.WaitReadyAsync(_options.PingTimeout, ct).ConfigureAwait(false);
        if (!ready)
        {
            throw new ProviderNotReadyException(
                ProviderId,
                "Local runtime is still warming up");
        }
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

            await Task.Delay(ReadinessPollInterval, ct).ConfigureAwait(false);
        }
    }
}
