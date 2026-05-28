using System.Net;
using System.Net.Sockets;
using System.Text;
using k8s;
using k8s.Autorest;
using k8s.KubeConfigModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scout.Core.Features;

namespace Scout.Plugin.Kubernetes.Internal;

/// <summary>
/// <see cref="IFeatureCredentialValidator"/> implementation for the
/// Kubernetes feature. Probes the cluster's /version endpoint without
/// caching the resulting client.
/// </summary>
public sealed class KubeconfigValidator : IFeatureCredentialValidator
{
    /// <summary>Body key carrying the kubeconfig YAML.</summary>
    public const string KubeconfigKey = "kubeconfig";

    /// <summary>Body key carrying the optional context override.</summary>
    public const string ContextKey = "context";

    private readonly IOptions<KubernetesPluginOptions> _options;
    private readonly ILogger<KubeconfigValidator> _logger;
    private readonly Func<KubernetesClientConfiguration, IKubernetes> _clientBuilder;

    /// <summary>Production constructor.</summary>
    public KubeconfigValidator(
        IOptions<KubernetesPluginOptions> options,
        ILogger<KubeconfigValidator> logger)
        : this(options, logger, clientBuilder: null)
    {
    }

    /// <summary>Test seam allowing a synthetic probe client.</summary>
    internal KubeconfigValidator(
        IOptions<KubernetesPluginOptions> options,
        ILogger<KubeconfigValidator> logger,
        Func<KubernetesClientConfiguration, IKubernetes>? clientBuilder)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _clientBuilder = clientBuilder ?? (cfg => new k8s.Kubernetes(cfg));
    }

    /// <inheritdoc/>
    public string FeatureId => "kubernetes";

    /// <inheritdoc/>
    public async Task<FeatureCredentialValidationResult> ValidateAsync(
        IReadOnlyDictionary<string, string> credentialBody,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialBody);

        if (!credentialBody.TryGetValue(KubeconfigKey, out var yaml) || string.IsNullOrWhiteSpace(yaml))
        {
            return Malformed("Kubeconfig is required.");
        }

        K8SConfiguration parsed;
        try
        {
            using var parseStream = new MemoryStream(Encoding.UTF8.GetBytes(yaml));
            parsed = KubernetesClientConfiguration.LoadKubeConfig(parseStream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Malformed(ex.Message);
        }

        if (parsed.Users is not null)
        {
            foreach (var user in parsed.Users)
            {
                var creds = user?.UserCredentials;
                if (creds?.ExternalExecution is not null)
                {
                    return new FeatureCredentialValidationResult(
                        FeatureCredentialValidationOutcome.UnsupportedAuth,
                        "Exec-plugin auth (gke-gcloud-auth-plugin, aws-iam-authenticator, etc.) is not supported. Embed inline credentials (certificate-authority-data, client-certificate-data, token) before using Scout.");
                }

                if (HasExternalFileReference(creds))
                {
                    return Malformed("Kubeconfig references external files. Embed credentials inline with --flatten.");
                }
            }
        }

        if (parsed.Clusters is not null)
        {
            foreach (var cluster in parsed.Clusters)
            {
                var ep = cluster?.ClusterEndpoint;
                if (ep is null)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(ep.CertificateAuthority)
                    && string.IsNullOrWhiteSpace(ep.CertificateAuthorityData))
                {
                    return Malformed("Kubeconfig references external files. Embed credentials inline with --flatten.");
                }
            }
        }

        credentialBody.TryGetValue(ContextKey, out var contextOverride);

        KubernetesClientConfiguration config;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(yaml));
            config = KubernetesClientConfiguration.BuildConfigFromConfigFile(
                kubeconfig: stream,
                currentContext: string.IsNullOrWhiteSpace(contextOverride) ? null : contextOverride,
                masterUrl: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Malformed(ex.Message);
        }

        config.HttpClientTimeout = _options.Value.DefaultTimeout;

        IKubernetes? probe = null;
        try
        {
            probe = _clientBuilder(config);

            var versionInfo = await probe.Version.GetCodeAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                _ = await probe.CoreV1.ListNamespaceAsync(
                    limit: 1,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (HttpOperationException nsEx) when (IsForbidden(nsEx))
            {
                return new FeatureCredentialValidationResult(
                    FeatureCredentialValidationOutcome.MissingScopes,
                    "Cluster allows /version anonymously but rejects namespace listing.");
            }

            var contextNames = string.Join(",", KubeconfigContextLister.ListContexts(yaml));
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["serverVersion"] = versionInfo?.GitVersion ?? string.Empty,
                ["contexts"] = contextNames,
            };

            return new FeatureCredentialValidationResult(
                FeatureCredentialValidationOutcome.Valid,
                versionInfo?.GitVersion,
                metadata);
        }
        catch (HttpOperationException ex) when (IsUnauthorizedOrForbidden(ex))
        {
            return new FeatureCredentialValidationResult(
                FeatureCredentialValidationOutcome.MissingScopes,
                "Cluster rejected the supplied credentials (HTTP " + (int?)ex.Response?.StatusCode + ").");
        }
        catch (HttpRequestException ex)
        {
            return Unreachable(ex.Message);
        }
        catch (SocketException ex)
        {
            return Unreachable(ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable("Validation probe timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unexpected error during kubeconfig validation.");
            return new FeatureCredentialValidationResult(
                FeatureCredentialValidationOutcome.TransientError,
                ex.Message);
        }
        finally
        {
            probe?.Dispose();
        }
    }

    private static bool HasExternalFileReference(UserCredentials? creds)
    {
        if (creds is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(creds.ClientCertificate)
            && string.IsNullOrWhiteSpace(creds.ClientCertificateData))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(creds.ClientKey)
            && string.IsNullOrWhiteSpace(creds.ClientKeyData))
        {
            return true;
        }

        return false;
    }

    private static bool IsUnauthorizedOrForbidden(HttpOperationException ex)
        => ex.Response?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool IsForbidden(HttpOperationException ex)
        => ex.Response?.StatusCode is HttpStatusCode.Forbidden;

    private static FeatureCredentialValidationResult Malformed(string detail)
        => new(FeatureCredentialValidationOutcome.Malformed, detail);

    private static FeatureCredentialValidationResult Unreachable(string detail)
        => new(FeatureCredentialValidationOutcome.Unreachable, detail);
}
