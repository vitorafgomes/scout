using System.Net;

namespace Scout.Plugin.WhatToWatch.Tests.Fakes;

/// <summary>
/// Test handler controlling the HTTP boundary. Each enqueued response supplies a
/// status code and JSON body; a response may instead be configured to throw,
/// simulating a timeout (<see cref="TaskCanceledException"/>) or a transport
/// failure (<see cref="HttpRequestException"/>). Captures every request so tests
/// can assert headers and the requested paths across multi-call operations.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public HttpRequestMessage? LastRequest { get; private set; }

    public List<Uri> RequestedUris { get; } = [];

    public void EnqueueJson(HttpStatusCode status, string json, Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json),
            };
            configure?.Invoke(response);
            return response;
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
        if (request.RequestUri is not null)
        {
            RequestedUris.Add(request.RequestUri);
        }

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No stub response enqueued.");
        }

        var factory = _responses.Dequeue();
        return Task.FromResult(factory(request));
    }
}
