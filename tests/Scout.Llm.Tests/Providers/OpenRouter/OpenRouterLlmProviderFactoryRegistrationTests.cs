using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.OpenRouter;

namespace Scout.Llm.Tests.Providers.OpenRouter;

/// <summary>
/// Registration tests for the OpenRouter provider factory. The internal HttpClient
/// seam keeps DI resolution network-free: the SK OpenAI connector services are
/// built against a stub-backed client and never reach a live endpoint.
/// </summary>
public sealed class OpenRouterLlmProviderFactoryRegistrationTests
{
    private static OpenRouterLlmProviderFactory CreateFactory(HttpClient httpClient) =>
        new(
            Options.Create(new LlmOptions
            {
                Provider = "OpenRouter",
                OpenRouter = new OpenRouterOptions
                {
                    ApiKey = "test-key",
                    Chat = new ChatModelOptions { Model = "openai/gpt-4o-mini" },
                    Embeddings = new OpenRouterEmbeddingOptions
                    {
                        Model = "text-embedding-3-small",
                        Endpoint = "https://api.openai.com/v1",
                        ApiKey = "embeddings-key",
                    },
                },
            }),
            httpClient);

    private static HttpClient StubClient() =>
        new(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://openrouter.ai/"),
        };

    [Fact]
    public void AddToKernel_Registers_ChatCompletion_Service()
    {
        using var client = StubClient();
        var builder = Kernel.CreateBuilder();

        CreateFactory(client).AddToKernel(builder);
        var kernel = builder.Build();

        kernel.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void AddToKernel_Registers_Embedding_Generator()
    {
        using var client = StubClient();
        var builder = Kernel.CreateBuilder();

        CreateFactory(client).AddToKernel(builder);
        var kernel = builder.Build();

        kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .ShouldNotBeNull();
    }

    [Fact]
    public void CreateEmbeddingGenerator_Returns_NonNull_Without_Shared_Kernel()
    {
        using var client = StubClient();

        var embedder = CreateFactory(client).CreateEmbeddingGenerator();

        embedder.ShouldNotBeNull();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("stub: no network in tests");
    }
}
