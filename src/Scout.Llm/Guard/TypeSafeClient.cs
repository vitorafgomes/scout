using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;

namespace Scout.Llm.Guard;

/// <summary>
/// Minimal client for TypeSafe's System One endpoint. Asks one yes-or-no ("noul")
/// question about a state (a string or a JSON-serializable object) and returns the
/// probability of yes. Every failure
/// becomes a <see cref="TypeSafeException"/> whose message never contains the key.
/// </summary>
internal sealed class TypeSafeClient(HttpClient http, IOptions<TypeSafeGuardOptions> options)
{
    internal const string QuestionName = "executive_write";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly TypeSafeGuardOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<double> AskNoulAsync(
        object state,
        string instructions,
        string criteriaTrue,
        string criteriaFalse,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(_options.Endpoint.TrimEnd('/') + "/"), "v1/systemone"))
        {
            Content = JsonContent.Create(new
            {
                model = _options.Model,
                state,
                questions = new Dictionary<string, object>
                {
                    [QuestionName] = new
                    {
                        type = "noul",
                        instructions,
                        criteria = new Dictionary<string, string>
                        {
                            ["true"] = criteriaTrue,
                            ["false"] = criteriaFalse,
                        },
                    },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await FailureAsync(response, cancellationToken).ConfigureAwait(false);
            }

            var body = await response.Content
                .ReadFromJsonAsync<SystemOneResponse>(cancellationToken).ConfigureAwait(false);
            return body?.Answers?.GetValueOrDefault(QuestionName)?.Noul
                ?? throw new TypeSafeException(
                    "TypeSafe returned no answer to the question.", response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            throw new TypeSafeException("TypeSafe could not be reached.", ex.StatusCode, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TypeSafeException("TypeSafe did not answer in time.", null, ex);
        }
        catch (JsonException ex)
        {
            throw new TypeSafeException("TypeSafe returned an unreadable answer.", null, ex);
        }
    }

    private static async Task<TypeSafeException> FailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.UnprocessableEntity)
        {
            var detail = await ValidationMessagesAsync(response, cancellationToken).ConfigureAwait(false);
            return new TypeSafeException(
                $"TypeSafe rejected the request ({(int)status}): {detail}", status);
        }

        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new TypeSafeException($"TypeSafe refused the API key ({(int)status}).", status)
            : new TypeSafeException($"TypeSafe request failed ({(int)status}).", status);
    }

    private static async Task<string> ValidationMessagesAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content
                .ReadFromJsonAsync<ValidationResponse>(cancellationToken).ConfigureAwait(false);
            var messages = body?.Detail?
                .Select(item => item.Msg)
                .Where(msg => !string.IsNullOrWhiteSpace(msg))
                .ToArray();
            return messages is { Length: > 0 } ? string.Join("; ", messages) : "no detail";
        }
        catch (JsonException)
        {
            return "no detail";
        }
    }

    private sealed record SystemOneResponse(
        [property: JsonPropertyName("answers")] Dictionary<string, NoulAnswer>? Answers);

    private sealed record NoulAnswer(
        [property: JsonPropertyName("noul")] double? Noul);

    private sealed record ValidationResponse(
        [property: JsonPropertyName("detail")] List<ValidationItem>? Detail);

    private sealed record ValidationItem(
        [property: JsonPropertyName("msg")] string? Msg);
}
