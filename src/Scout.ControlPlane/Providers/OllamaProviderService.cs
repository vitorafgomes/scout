using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.ControlPlane.Providers;

/// <summary>
/// Composes <see cref="IBinaryLocator"/>, <see cref="IOllamaApiClient"/>,
/// <see cref="IProcessLauncher"/>, <see cref="ILocalRuntimeRegistry"/> and
/// <see cref="ILocalRuntimeConfigStore"/> to expose the live status plus
/// start/restart lifecycle for the local Ollama runtime.
/// </summary>
public sealed class OllamaProviderService(
    IBinaryLocator locator,
    IOllamaApiClient api,
    IProcessLauncher launcher,
    ILocalRuntimeRegistry registry,
    ILocalRuntimeConfigStore configStore,
    IOptions<OllamaProviderOptions> options,
    IOllamaReadinessProbe probe,
    ILogger<OllamaProviderService> logger) : IDisposable
{
    public const string ProviderId = "ollama";
    private const string BinaryName = "ollama";

    // Mirror of Scout.Host.ControlPlane.Security.SessionToken.EnvVarName. The
    // domain project cannot reference the host, so the literal is duplicated;
    // a host-side test asserts the two stay in sync. Scrubbed from every child
    // launch so the session token is not inherited by spawned daemons.
    internal const string SessionTokenEnvVarName = "SCOUT_CONTROLPLANE_TOKEN";
    internal const string WorkerTokenEnvVarName = "SCOUT_CONTROLPLANE_WORKER_TOKEN";

    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExternalStopMaxWait = TimeSpan.FromSeconds(10);

    private readonly IBinaryLocator _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    private readonly IOllamaApiClient _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly IProcessLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    private readonly ILocalRuntimeRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly ILocalRuntimeConfigStore _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
    private readonly OllamaProviderOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IOllamaReadinessProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    private readonly ILogger<OllamaProviderService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private static readonly string[] ServeArguments = ["serve"];

    private readonly SemaphoreSlim _startGate = new(1, 1);

    public void Dispose() => _startGate.Dispose();

    public Task<ProviderStatus> GetStatusAsync(CancellationToken ct) =>
        GetStatusInternalAsync(awaitReadiness: true, ct);

    internal async Task<ProviderStatus> GetStatusInternalAsync(bool awaitReadiness, CancellationToken ct)
    {
        var managed = _registry.GetOwned(ProviderId) is not null;
        var autostart = await _configStore.GetAutostartAsync(ProviderId, ct).ConfigureAwait(false);

        if (awaitReadiness && _probe.Current != OllamaReadinessState.Failed)
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
            var status = await StartInternalAsync(skipReadinessCheck: true, ct).ConfigureAwait(false);
            if (status.Running)
            {
                _probe.MarkReady();
            }
            else
            {
                _probe.MarkFailed();
            }
            return status;
        }
        catch
        {
            _probe.MarkFailed();
            throw;
        }
    }

    internal async Task<ProviderStatus> StartInternalAsync(bool skipReadinessCheck, CancellationToken ct)
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
        var status = await GetStatusInternalAsync(awaitReadiness: false, ct).ConfigureAwait(false);
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
            status = await GetStatusInternalAsync(awaitReadiness: false, ct).ConfigureAwait(false);
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
                return await GetStatusInternalAsync(awaitReadiness: false, ct).ConfigureAwait(false);
            }

            var request = new ProcessLaunchRequest(
                FileName: binaryPath,
                Arguments: ServeArguments,
                // Scrub the control-plane session token so it is not inherited
                // by the daemon (visible via /proc/<pid>/environ). Null value
                // tells the launcher to remove the variable from the child env.
                EnvironmentOverrides: new Dictionary<string, string?>
                {
                    [SessionTokenEnvVarName] = null,
                    [WorkerTokenEnvVarName] = null,
                });

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

            return await GetStatusInternalAsync(awaitReadiness: false, ct).ConfigureAwait(false);
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
        var status = await GetStatusInternalAsync(awaitReadiness: false, ct).ConfigureAwait(false);
        if (!status.Running)
        {
            return status;
        }

        var owned = _registry.GetOwned(ProviderId);
        if (owned is null)
        {
            // Externally-started daemon: Scout did not spawn this process and
            // must not kill what it does not own (boundary cases default to
            // forbidden). Surface a clear, non-fatal status instead of a
            // name-globbed kill that could take down an unrelated process.
            _logger.LogInformation(
                "Ollama is running but was not started by Scout; refusing to stop an externally-managed daemon");

            return status with
            {
                LastError = new ProviderError(
                    Code: "providers/externally-managed",
                    Message: "Ollama is running but was not started by Scout; it must be stopped externally."),
            };
        }

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

        // Poll until the daemon stops answering /api/tags as the real boundary
        // check that the owned process actually released the endpoint.
        var deadline = DateTimeOffset.UtcNow + ExternalStopMaxWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var tags = await _api.GetTagsAsync(ct).ConfigureAwait(false);
            if (tags is null)
            {
                return await GetStatusInternalAsync(awaitReadiness: false, ct).ConfigureAwait(false);
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
        _logger.LogInformation("Downloading local model {Model}", modelName);
        var finished = false;
        try
        {
            await EnsureReadyOrThrowAsync(ct).ConfigureAwait(false);
            await foreach (var ev in _api.PullAsync(modelName, ct).ConfigureAwait(false))
            {
                yield return ev;
            }
            finished = true;
        }
        finally
        {
            if (finished)
            {
                _logger.LogInformation("Downloaded local model {Model}", modelName);
            }
            else
            {
                _logger.LogWarning("Download of local model {Model} did not finish", modelName);
            }
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
        try
        {
            await EnsureReadyOrThrowAsync(ct).ConfigureAwait(false);
            await _api.DeleteModelAsync(modelName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete local model {Model}", modelName);
            throw;
        }
        _logger.LogInformation("Deleted local model {Model}", modelName);
    }

    public Task SetAutostartAsync(bool enabled, CancellationToken ct) =>
        _configStore.SetAutostartAsync(ProviderId, enabled, ct);

    private async Task EnsureReadyOrThrowAsync(CancellationToken ct)
    {
        // Cold-start fallback: the readiness gate only completes once a managed
        // Start drives Starting->Ready. When the worker never started the
        // daemon (state Unknown, or Failed after a start) but Ollama is already running
        // externally, blocking on the gate would time out and surface a
        // spurious ProviderNotReadyException. In that case probe the daemon
        // directly: if /api/tags answers, the runtime is usable - proceed.
        if (_probe.Current is OllamaReadinessState.Unknown or OllamaReadinessState.Failed)
        {
            var tags = await _api.GetTagsAsync(ct).ConfigureAwait(false);
            if (tags is not null)
            {
                return;
            }
        }

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
