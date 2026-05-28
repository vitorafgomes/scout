using System.Security.Cryptography;
using System.Text;
using k8s;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Core.Features;

namespace Scout.Plugin.Kubernetes.Internal;

/// <summary>
/// Default <see cref="IKubernetesClientFactory"/>. Reads the kubeconfig
/// YAML and optional context override from <see cref="ICredentialStore"/>,
/// builds an <see cref="IKubernetes"/> client without ever writing the
/// kubeconfig to disk, and caches it per credential snapshot.
/// </summary>
public sealed class KubernetesClientFactory : IKubernetesClientFactory, IDisposable
{
    private readonly ICredentialStore _credentialStore;
    private readonly IOptions<KubernetesPluginOptions> _options;
    private readonly ILogger<KubernetesClientFactory> _logger;
    private readonly Func<string, string?, IKubernetes> _clientBuilder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cacheKey;
    private IKubernetes? _cached;

    /// <summary>Production constructor.</summary>
    public KubernetesClientFactory(
        ICredentialStore credentialStore,
        IOptions<KubernetesPluginOptions> options,
        ILogger<KubernetesClientFactory> logger)
        : this(credentialStore, options, logger, clientBuilder: null)
    {
    }

    /// <summary>
    /// Test seam: lets unit tests inject a synthetic client builder so the
    /// factory caching and credential plumbing can be exercised without
    /// touching the real KubernetesClient pipeline.
    /// </summary>
    internal KubernetesClientFactory(
        ICredentialStore credentialStore,
        IOptions<KubernetesPluginOptions> options,
        ILogger<KubernetesClientFactory> logger,
        Func<string, string?, IKubernetes>? clientBuilder)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _credentialStore = credentialStore;
        _options = options;
        _logger = logger;
        _clientBuilder = clientBuilder ?? DefaultClientBuilder;
    }

    /// <inheritdoc/>
    public async Task<IKubernetes> GetClientAsync(CancellationToken cancellationToken)
    {
        var opts = _options.Value;

        var yaml = await _credentialStore.GetAsync(opts.KubeconfigCredentialKey, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidOperationException(
                "Kubernetes feature is enabled but no kubeconfig is stored. Provide credentials in the Features tab.");
        }

        var context = await _credentialStore.GetAsync(opts.ContextCredentialKey, cancellationToken)
            .ConfigureAwait(false);

        var key = ComputeCacheKey(yaml, context);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && string.Equals(_cacheKey, key, StringComparison.Ordinal))
            {
                return _cached;
            }

            _cached?.Dispose();
            _cached = _clientBuilder(yaml, string.IsNullOrWhiteSpace(context) ? null : context);
            _cacheKey = key;
            _logger.LogDebug("Built new Kubernetes client (cache key: {Key}).", key[..8]);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Invalidate()
    {
        _gate.Wait();
        try
        {
            _cached?.Dispose();
            _cached = null;
            _cacheKey = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cached?.Dispose();
        _gate.Dispose();
    }

    private IKubernetes DefaultClientBuilder(string yaml, string? context)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(yaml));
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(
            kubeconfig: stream,
            currentContext: context,
            masterUrl: null);
        config.HttpClientTimeout = _options.Value.DefaultTimeout;
        return new k8s.Kubernetes(config);
    }

    private static string ComputeCacheKey(string yaml, string? context)
    {
        var payload = $"{yaml}{context ?? string.Empty}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }
}
