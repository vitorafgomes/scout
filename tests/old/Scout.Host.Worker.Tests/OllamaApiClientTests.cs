using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OllamaApiClientTests
{
    private static OllamaApiClient BuildClient(HttpMessageHandler handler, TimeSpan? pingTimeout = null)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = pingTimeout ?? TimeSpan.FromSeconds(5),
        };
        var options = Options.Create(new OllamaProviderOptions
        {
            Endpoint = "http://localhost:11434",
            PingTimeout = pingTimeout ?? TimeSpan.FromSeconds(2),
        });
        return new OllamaApiClient(http, options, NullLogger<OllamaApiClient>.Instance);
    }

    [Fact]
    public async Task GetTagsAsync_DaemonReturns200WithModels_ReturnsParsed()
    {
        const string body = """
        {
          "models": [
            { "name": "qwen2.5:3b", "modified_at": "2026-05-22T10:00:00Z", "size": 1936000000 },
            { "name": "llama3.1:8b", "modified_at": "2026-05-20T08:00:00Z", "size": 4700000000 }
          ]
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        var client = BuildClient(handler);

        var result = await client.GetTagsAsync(CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Models.Count.ShouldBe(2);
        result.Models[0].Name.ShouldBe("qwen2.5:3b");
        result.Models[0].SizeBytes.ShouldBe(1936000000L);
        result.Models[0].ModifiedAt.ShouldBe("2026-05-22T10:00:00Z");
        result.Models[1].Name.ShouldBe("llama3.1:8b");
    }

    [Fact]
    public async Task GetTagsAsync_DaemonRefused_ReturnsNull()
    {
        // Simulate a connection-refused condition with the exception shape
        // HttpClient surfaces in that case.
        var handler = new ThrowingHandler(new HttpRequestException(
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));
        var client = BuildClient(handler);

        var result = await client.GetTagsAsync(CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetTagsAsync_DaemonTimesOut_ReturnsNull()
    {
        // The handler sleeps past the configured timeout; the linked CTS
        // inside the client must cancel and surface as null.
        var handler = new DelayHandler(TimeSpan.FromSeconds(5));
        var client = BuildClient(handler, pingTimeout: TimeSpan.FromMilliseconds(150));

        var result = await client.GetTagsAsync(CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetTagsAsync_DaemonReturns500_ReturnsNull()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });
        var client = BuildClient(handler);

        var result = await client.GetTagsAsync(CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetTagsAsync_MalformedJson_ReturnsNull()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not valid json", Encoding.UTF8, "application/json"),
        });
        var client = BuildClient(handler);

        var result = await client.GetTagsAsync(CancellationToken.None);

        result.ShouldBeNull();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;

        public ThrowingHandler(Exception ex) => _ex = ex;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_ex);
    }

    private sealed class DelayHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public DelayHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
