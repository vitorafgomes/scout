using System.Net;

namespace Scout.Plugin.YouTube.Tests.Fakes;

/// <summary>
/// Test handler controlling the HTTP boundary. Answers from a responder when one is given,
/// otherwise from a queue of responses; a queued response may throw instead, simulating a
/// timeout (<see cref="TaskCanceledException"/>) or a transport failure
/// (<see cref="HttpRequestException"/>). Records every request it receives and, like the real
/// socket handler, links each response to its request.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public void EnqueueJson(HttpStatusCode status, string json)
        => _responses.Enqueue(_ => Json(status, json));

    public void EnqueueThrow(Exception exception)
        => _responses.Enqueue(_ => throw exception);

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json) };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            string.Join("\n", request.Headers.SelectMany(h => h.Value))));

        if (respond is null && _responses.Count == 0)
        {
            throw new InvalidOperationException("No stub response enqueued.");
        }

        var response = respond is null ? _responses.Dequeue()(request) : respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>What the stub saw of one request: method, full URI, Authorization and every header value.</summary>
internal sealed record RecordedRequest(string Method, Uri Uri, string? Authorization, string HeaderValues);
