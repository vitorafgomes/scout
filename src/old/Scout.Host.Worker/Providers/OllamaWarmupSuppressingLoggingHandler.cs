using Microsoft.Extensions.Logging;

namespace Scout.Host.Worker.Providers;

/// <summary>
/// Replaces the default HttpClient logging handler for the Ollama API client
/// so transport failures during the warmup window are silenced (logged at
/// Debug, no stack). Failures after the probe is Ready get full Warning
/// stacks because they indicate a real outage.
/// </summary>
public sealed class OllamaWarmupSuppressingLoggingHandler : DelegatingHandler
{
    private readonly IOllamaReadinessProbe _probe;
    private readonly ILogger<OllamaWarmupSuppressingLoggingHandler> _logger;

    public OllamaWarmupSuppressingLoggingHandler(
        IOllamaReadinessProbe probe,
        ILogger<OllamaWarmupSuppressingLoggingHandler> logger)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var method = request.Method.Method;
            var uri = request.RequestUri?.ToString() ?? "<unknown>";
            if (!_probe.IsReady)
            {
                _logger.LogDebug(
                    "Ollama probe still warming up; suppressed transport failure for {Method} {Uri}: {Message}",
                    method,
                    uri,
                    ex.Message);
            }
            else
            {
                _logger.LogWarning(
                    ex,
                    "Ollama transport failure for {Method} {Uri}",
                    method,
                    uri);
            }
            throw;
        }
    }
}
