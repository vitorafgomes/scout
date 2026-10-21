using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;

namespace Scout.Llm.Tests.Providers.Ollama;

/// <summary>
/// Hermetic HTTP-boundary tests. The factory's internal HttpClient seam lets us
/// drive the SK Ollama connector with a stub handler, so the chat and embedding
/// services are exercised without a real Ollama daemon. The contract: a failing
/// boundary surfaces an exception rather than hanging or returning a silent
/// empty result.
/// </summary>
public sealed class OllamaHttpBoundaryFailureTests
{
    private static OllamaLlmProviderFactory CreateFactory(HttpClient httpClient) =>
        new(
            Options.Create(new LlmOptions
            {
                Provider = "Ollama",
                Ollama = new OllamaOptions
                {
                    Endpoint = "http://localhost:11434",
                    Chat = new ChatModelOptions { Model = "llama3.1" },
                    Embeddings = new EmbeddingModelOptions { Model = "nomic-embed-text" },
                },
            }),
            httpClient);

    private static Kernel BuildKernel(HttpClient httpClient)
    {
        var builder = Kernel.CreateBuilder();
        CreateFactory(httpClient).AddToKernel(builder);
        return builder.Build();
    }

    [Fact]
    public async Task ChatCompletion_Surfaces_Failure_On_ServerError()
    {
        using var handler = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            });
        using var client = new HttpClient(handler);
        var chat = BuildKernel(client).GetRequiredService<IChatCompletionService>();

        await Should.ThrowAsync<Exception>(() =>
            chat.GetChatMessageContentAsync("hello"));
    }

    [Fact]
    public async Task ChatCompletion_Surfaces_Failure_On_ConnectionDrop()
    {
        using var handler = new StubHttpMessageHandler(
            _ => throw new HttpRequestException("connection reset"));
        using var client = new HttpClient(handler);
        var chat = BuildKernel(client).GetRequiredService<IChatCompletionService>();

        await Should.ThrowAsync<Exception>(() =>
            chat.GetChatMessageContentAsync("hello"));
    }

    [Fact]
    public async Task ChatCompletion_Surfaces_Failure_On_Timeout()
    {
        using var handler = new StubHttpMessageHandler(
            _ => throw new TaskCanceledException("timeout"));
        using var client = new HttpClient(handler);
        var chat = BuildKernel(client).GetRequiredService<IChatCompletionService>();

        await Should.ThrowAsync<Exception>(() =>
            chat.GetChatMessageContentAsync("hello"));
    }

    [Fact]
    public async Task Embedding_Surfaces_Failure_On_ServerError()
    {
        using var handler = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            });
        using var client = new HttpClient(handler);
        var embeddings = BuildKernel(client)
            .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        await Should.ThrowAsync<Exception>(() =>
            embeddings.GenerateAsync(["hello"]));
    }

    [Fact]
    public async Task Embedding_Surfaces_Failure_On_ConnectionDrop()
    {
        using var handler = new StubHttpMessageHandler(
            _ => throw new HttpRequestException("connection reset"));
        using var client = new HttpClient(handler);
        var embeddings = BuildKernel(client)
            .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        await Should.ThrowAsync<Exception>(() =>
            embeddings.GenerateAsync(["hello"]));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
