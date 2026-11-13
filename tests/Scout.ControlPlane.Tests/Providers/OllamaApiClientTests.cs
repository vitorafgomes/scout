using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.ControlPlane.Providers;
using Shouldly;
using Xunit;

namespace Scout.ControlPlane.Tests.Providers;

public sealed class OllamaApiClientTests
{
    private sealed class StreamHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson"),
            });
    }

    [Fact]
    public async Task A_Pull_Stream_Error_Line_Fails_The_Pull_With_That_Error_Text()
    {
        using var http = new HttpClient(new StreamHandler(
            "{\"status\":\"pulling manifest\"}\n{\"error\":\"pull model manifest: file does not exist\"}\n"));
        var client = new OllamaApiClient(http, Options.Create(new OllamaProviderOptions()), NullLogger<OllamaApiClient>.Instance);
        var seen = new List<string>();

        var failure = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var progress in client.PullAsync("missing-model", CancellationToken.None))
            {
                seen.Add(progress.Status);
            }
        });

        failure.Message.ShouldBe("Ollama /api/pull failed: pull model manifest: file does not exist");
        seen.ShouldBe(["pulling manifest"]);
    }
}
