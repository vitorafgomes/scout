using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Host.Worker.Providers;

/// <summary>
/// HttpClient-backed Ollama API client. Owns the <c>/api/tags</c> probe used
/// by <see cref="OllamaProviderService"/>. Every failure path returns null.
/// </summary>
public sealed class OllamaApiClient : IOllamaApiClient
{
    private readonly HttpClient _http;
    private readonly OllamaProviderOptions _options;
    private readonly ILogger<OllamaApiClient> _logger;

    public OllamaApiClient(
        HttpClient http,
        IOptions<OllamaProviderOptions> options,
        ILogger<OllamaApiClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OllamaTags?> GetTagsAsync(CancellationToken ct)
    {
        // Bound the call to the configured ping timeout even if the HttpClient
        // was constructed with a longer ceiling. Linked CTS keeps the caller's
        // cancellation in effect.
        using var timeoutCts = new CancellationTokenSource(_options.PingTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var baseUri = new Uri(_options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        var tagsUri = new Uri(baseUri, "api/tags");

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(tagsUri, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Ollama /api/tags timed out after {Timeout}", _options.PingTimeout);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Ollama /api/tags request failed");
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Ollama /api/tags returned {Status}", (int)response.StatusCode);
                return null;
            }

            OllamaTagsResponse? payload;
            try
            {
                payload = await response.Content
                    .ReadFromJsonAsync<OllamaTagsResponse>(JsonOptions, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Ollama /api/tags returned malformed JSON");
                return null;
            }
            catch (NotSupportedException ex)
            {
                _logger.LogDebug(ex, "Ollama /api/tags returned non-JSON content");
                return null;
            }

            if (payload?.Models is null)
            {
                return new OllamaTags(Array.Empty<ProviderModel>());
            }

            var models = payload.Models
                .Select(m => new ProviderModel(
                    Name: m.Name ?? string.Empty,
                    SizeBytes: m.Size,
                    ModifiedAt: m.ModifiedAt ?? string.Empty))
                .ToArray();

            return new OllamaTags(models);
        }
    }

    public async IAsyncEnumerable<PullProgress> PullAsync(
        string modelName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var baseUri = new Uri(_options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        var uri = new Uri(baseUri, "api/pull");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new { name = modelName, stream = true }),
        };

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Ollama /api/pull returned {(int)response.StatusCode}: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            PullProgressPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<PullProgressPayload>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Ollama /api/pull yielded malformed JSON line, skipping");
                continue;
            }

            if (payload is null || string.IsNullOrEmpty(payload.Status))
            {
                continue;
            }

            yield return new PullProgress(
                Status: payload.Status,
                Digest: payload.Digest,
                Total: payload.Total,
                Completed: payload.Completed);
        }
    }

    public async Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var baseUri = new Uri(_options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        var uri = new Uri(baseUri, "api/delete");

        using var request = new HttpRequestMessage(HttpMethod.Delete, uri)
        {
            Content = JsonContent.Create(new { name = modelName }),
        };

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Ollama /api/delete returned {(int)response.StatusCode}: {body}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record PullProgressPayload(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("total")] long? Total,
        [property: JsonPropertyName("completed")] long? Completed);

    internal sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<OllamaTagModel>? Models);

    internal sealed record OllamaTagModel(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("modified_at")] string? ModifiedAt);
}
