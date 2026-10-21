using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.AzureAIInference;

namespace Scout.Llm.Tests.Providers.AzureAIInference;

/// <summary>
/// Registration tests for the Azure AI Inference provider factory. The internal
/// HttpClient seam keeps DI resolution network-free: the SK Azure AI Inference
/// connector services are built against a stub-backed client and never reach the
/// live endpoint.
/// </summary>
public sealed class AzureAIInferenceLlmProviderFactoryRegistrationTests
{
    private static AzureAIInferenceLlmProviderFactory CreateFactory(HttpClient httpClient) =>
        new(
            Options.Create(new LlmOptions
            {
                Provider = "AzureAIInference",
                AzureAIInference = new AzureAIInferenceOptions
                {
                    ApiKey = "test-key",
                    Endpoint = "https://my-inference.services.ai.azure.com/models",
                    Chat = new ChatModelOptions { Model = "Phi-3.5-MoE-instruct" },
                    Embeddings = new EmbeddingModelOptions { Model = "text-embedding-3-small" },
                },
            }),
            httpClient);

    private static HttpClient StubClient() =>
        new(new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://my-inference.services.ai.azure.com/"),
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
