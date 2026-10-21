using System.Net;

namespace Scout.Plugin.Markets.Tests.Fakes;

/// <summary>
/// Test handler controlling the HTTP boundary. Each enqueued response supplies a
/// status code and body; a response may instead be configured to throw, simulating a
/// timeout (<see cref="TaskCanceledException"/>) or a transport failure
/// (<see cref="HttpRequestException"/>). Captures the last request and a request
/// count so tests can assert headers, the requested path, and caching behaviour.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public HttpRequestMessage? LastRequest { get; private set; }

    public int RequestCount { get; private set; }

    public void EnqueueBody(HttpStatusCode status, string body)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        });
    }

    public void EnqueueThrow(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        RequestCount++;
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No stub response enqueued.");
        }

        var factory = _responses.Dequeue();
        return Task.FromResult(factory(request));
    }
}
