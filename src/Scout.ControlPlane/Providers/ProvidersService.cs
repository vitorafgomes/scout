using System.Runtime.CompilerServices;

namespace Scout.ControlPlane.Providers;

/// <summary>
/// Domain facade over the provider operations. Ollama is fully wired
/// (status + lifecycle + model ops); LM Studio is a stub whose daemon
/// lifecycle is not under the worker's control. The gRPC service is a thin
/// adapter over this type.
/// </summary>
public sealed class ProvidersService
{
    /// <summary>Canonical id for the Ollama provider.</summary>
    public const string OllamaId = "ollama";

    /// <summary>Canonical id for the LM Studio provider.</summary>
    public const string LmStudioId = "lm-studio";

    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(2);

    private readonly OllamaProviderService _ollama;

    public ProvidersService(OllamaProviderService ollama)
    {
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
    }

    /// <summary>Snapshot every known provider.</summary>
    public async Task<IReadOnlyList<ProviderStatus>> ListAsync(CancellationToken ct)
    {
        var ollamaStatus = await _ollama.GetStatusAsync(ct).ConfigureAwait(false);
        return new[] { ollamaStatus, LmStudioStub() };
    }

    /// <summary>Snapshot a single provider; throws when the id is unknown.</summary>
    public async Task<ProviderStatus> GetStatusAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return id switch
        {
            OllamaId => await _ollama.GetStatusAsync(ct).ConfigureAwait(false),
            LmStudioId => LmStudioStub(),
            _ => throw new ProviderUnknownException(id),
        };
    }

    /// <summary>Start a provider; LM Studio lifecycle is unsupported.</summary>
    public async Task<ProviderStatus> StartAsync(string id, CancellationToken ct)
    {
        EnsureKnown(id);
        if (id == LmStudioId)
        {
            throw new ProviderLifecycleUnsupportedException(
                id, "LM Studio lifecycle control is not available on this install");
        }
        return await _ollama.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stop a provider; LM Studio lifecycle is unsupported.</summary>
    public async Task<ProviderStatus> StopAsync(string id, CancellationToken ct)
    {
        EnsureKnown(id);
        if (id == LmStudioId)
        {
            throw new ProviderLifecycleUnsupportedException(
                id, "LM Studio lifecycle control is not available on this install");
        }
        return await _ollama.StopAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Restart a provider; LM Studio lifecycle is unsupported.</summary>
    public async Task<ProviderStatus> RestartAsync(string id, CancellationToken ct)
    {
        EnsureKnown(id);
        if (id == LmStudioId)
        {
            throw new ProviderLifecycleUnsupportedException(
                id, "LM Studio lifecycle control is not available on this install");
        }
        return await _ollama.RestartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Persist the autostart flag for a known provider.</summary>
    public async Task SetAutostartAsync(string id, bool enabled, CancellationToken ct)
    {
        EnsureKnown(id);
        if (id == OllamaId)
        {
            await _ollama.SetAutostartAsync(enabled, ct).ConfigureAwait(false);
        }

        // LM Studio autostart is currently a no-op (no managed daemon), but
        // the id is accepted so the UI toggle round-trips without error.
    }

    /// <summary>Stream status snapshots for a watched provider until cancellation.</summary>
    public async IAsyncEnumerable<ProviderStatus> WatchAsync(
        string id,
        [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureKnown(id);

        while (!ct.IsCancellationRequested)
        {
            yield return await GetStatusAsync(id, ct).ConfigureAwait(false);

            try
            {
                await Task.Delay(WatchInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>Pull a model on a provider; LM Studio is unsupported.</summary>
    public IAsyncEnumerable<PullProgress> PullModelAsync(string id, string model, CancellationToken ct)
    {
        EnsureKnown(id);
        if (id == LmStudioId)
        {
            throw new ProviderLifecycleUnsupportedException(
                id, "LM Studio model pull is not available on this install");
        }
        return _ollama.PullModelAsync(model, ct);
    }

    /// <summary>List models on a provider; LM Studio is unsupported.</summary>
    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(string id, CancellationToken ct)
    {
        EnsureKnown(id);
        if (id == LmStudioId)
        {
            throw new ProviderLifecycleUnsupportedException(
                id, "LM Studio model listing is not available on this install");
        }
        return await _ollama.ListModelsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Delete a model on a provider; LM Studio is unsupported.</summary>
    public async Task DeleteModelAsync(string id, string model, CancellationToken ct)
    {
        EnsureKnown(id);
        if (id == LmStudioId)
        {
            throw new ProviderLifecycleUnsupportedException(
                id, "LM Studio model delete is not available on this install");
        }
        await _ollama.DeleteModelAsync(model, ct).ConfigureAwait(false);
    }

    private static void EnsureKnown(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id is not (OllamaId or LmStudioId))
        {
            throw new ProviderUnknownException(id);
        }
    }

    private static ProviderStatus LmStudioStub() => new(
        Id: LmStudioId,
        Installed: false,
        Running: false,
        Endpoint: "http://localhost:1234",
        Version: null,
        ManagedByWorker: false,
        Autostart: true,
        SupportsLifecycle: false,
        LastError: null,
        Models: Array.Empty<ProviderModel>());
}
