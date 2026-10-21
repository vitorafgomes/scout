using System.Net;
using System.Text;

namespace Scout.Plugin.WebSearch.Tests.Fakes;

/// <summary>
/// Test handler controlling the HTTP boundary. Each enqueued response supplies a
/// status code and body (with an optional media type); a response may instead be
/// configured to throw, simulating a timeout (<see cref="TaskCanceledException"/>)
/// or a transport failure (<see cref="HttpRequestException"/>). Captures the last
/// request so tests can assert headers and the requested URL.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public HttpRequestMessage? LastRequest { get; private set; }

    public void EnqueueJson(HttpStatusCode status, string json)
        => EnqueueContent(status, json, "application/json");

    public void EnqueueContent(HttpStatusCode status, string body, string mediaType)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
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
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No stub response enqueued.");
        }

        var factory = _responses.Dequeue();
        return Task.FromResult(factory(request));
    }
}
