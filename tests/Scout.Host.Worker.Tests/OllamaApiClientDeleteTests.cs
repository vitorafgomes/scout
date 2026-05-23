using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Host.Worker.Providers;
using Shouldly;

namespace Scout.Host.Worker.Tests;

public sealed class OllamaApiClientDeleteTests
{
    private static OllamaApiClient BuildClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var options = Options.Create(new OllamaProviderOptions
        {
            Endpoint = "http://localhost:11434",
            PingTimeout = TimeSpan.FromSeconds(2),
        });
        return new OllamaApiClient(http, options, NullLogger<OllamaApiClient>.Instance);
    }

    [Fact]
    public async Task DeleteModelAsync_ReturnsOnSuccess()
    {
        // Snapshot request details inside the handler because the production
        // code wraps the HttpRequestMessage in a `using` block, disposing the
        // JsonContent before the test gets a chance to read it.
        HttpMethod? capturedMethod = null;
        string? capturedPath = null;
        string? capturedBody = null;
        var handler = new CapturingHandler(req =>
        {
            capturedMethod = req.Method;
            capturedPath = req.RequestUri!.PathAndQuery;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = BuildClient(handler);

        await client.DeleteModelAsync("qwen2.5:3b", CancellationToken.None);

        capturedMethod.ShouldBe(HttpMethod.Delete);
        capturedPath.ShouldBe("/api/delete");
        capturedBody.ShouldNotBeNull();
        capturedBody!.ShouldContain("qwen2.5:3b");
    }

    [Fact]
    public async Task DeleteModelAsync_HttpError_Throws()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found"),
        });
        var client = BuildClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.DeleteModelAsync("missing", CancellationToken.None));

        ex.Message.ShouldContain("404");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_factory(request));
    }
}
