namespace Scout.Host.Worker.Providers;

/// <summary>Result of a successful call to Ollama's <c>/api/tags</c>.</summary>
public sealed record OllamaTags(IReadOnlyList<ProviderModel> Models);

/// <summary>One JSONL chunk emitted by Ollama's <c>/api/pull</c>.</summary>
public sealed record PullProgress(
    string Status,
    string? Digest,
    long? Total,
    long? Completed);

/// <summary>
/// Thin abstraction over Ollama's HTTP API used by the status endpoint.
/// Status-probe failures (connection refused, timeout, non-2xx, malformed
/// JSON) collapse to <c>null</c>. Pull / delete surface errors as exceptions
/// because callers need actionable feedback.
/// </summary>
public interface IOllamaApiClient
{
    Task<OllamaTags?> GetTagsAsync(CancellationToken ct);

    IAsyncEnumerable<PullProgress> PullAsync(string modelName, CancellationToken ct);

    Task DeleteModelAsync(string modelName, CancellationToken ct);
}
