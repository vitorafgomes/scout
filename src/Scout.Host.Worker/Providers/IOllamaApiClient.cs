namespace Scout.Host.Worker.Providers;

/// <summary>Result of a successful call to Ollama's <c>/api/tags</c>.</summary>
public sealed record OllamaTags(IReadOnlyList<ProviderModel> Models);

/// <summary>
/// Thin abstraction over Ollama's HTTP API used by the status endpoint.
/// All failure modes (connection refused, timeout, non-2xx, malformed JSON)
/// collapse to <c>null</c> so callers can treat "unreachable" uniformly.
/// </summary>
public interface IOllamaApiClient
{
    Task<OllamaTags?> GetTagsAsync(CancellationToken ct);
}
