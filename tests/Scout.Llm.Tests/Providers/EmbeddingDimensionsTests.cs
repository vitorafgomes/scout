using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.AzureOpenAI;
using Scout.Llm.Providers.OpenAI;
using Scout.Llm.Providers.OpenRouter;

namespace Scout.Llm.Tests.Providers;

/// <summary>
/// The OpenAI-family factories ask a text-embedding-3 model for the 768 values the project index stores, and never send
/// the dimensions option to a model that refuses it.
/// </summary>
public sealed class EmbeddingDimensionsTests
{
    private const string ApiKey = "sk-test-key-must-never-surface";

    private sealed class EmbeddingsEndpoint : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(Body);
            var size = json.RootElement.TryGetProperty("dimensions", out var dimensions) ? dimensions.GetInt32() : 1536;
            var vector = Convert.ToBase64String(MemoryMarshal.AsBytes(new float[size].AsSpan()));
            var reply = JsonSerializer.Serialize(new
            {
                @object = "list",
                data = new[] { new { @object = "embedding", index = 0, embedding = vector } },
                model = "text-embedding-3-small",
                usage = new { prompt_tokens = 1, total_tokens = 1 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private static IEmbeddingGenerator<string, Embedding<float>> OpenAI(string model, HttpClient http) =>
        new OpenAILlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = "OpenAI",
                OpenAI = new OpenAIOptions
                {
                    ApiKey = ApiKey,
                    Chat = new ChatModelOptions { Model = "gpt-4o-mini" },
                    Embeddings = new OpenAIEmbeddingOptions { Model = model },
                },
            }),
            http).CreateEmbeddingGenerator();

    private static IEmbeddingGenerator<string, Embedding<float>> OpenRouter(string model, HttpClient http) =>
        new OpenRouterLlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = "OpenRouter",
                OpenRouter = new OpenRouterOptions
                {
                    ApiKey = ApiKey,
                    Chat = new ChatModelOptions { Model = "openai/gpt-4o-mini" },
                    Embeddings = new OpenRouterEmbeddingOptions { Model = model, Endpoint = "https://api.openai.com/v1", ApiKey = ApiKey },
                },
            }),
            http).CreateEmbeddingGenerator();

    private static IEmbeddingGenerator<string, Embedding<float>> Azure(string deployment, HttpClient http) =>
        new AzureOpenAILlmProviderFactory(
            Options.Create(new LlmOptions
            {
                Provider = "AzureOpenAI",
                AzureOpenAI = new AzureOpenAIOptions
                {
                    ApiKey = ApiKey,
                    Endpoint = "https://my-aoai.openai.azure.com",
                    Chat = new AzureDeploymentOptions { Deployment = "gpt-4o" },
                    Embeddings = new AzureDeploymentOptions { Deployment = deployment },
                },
            }),
            http).CreateEmbeddingGenerator();

    private static async Task<(string Body, int Length)> Embed(string provider, string model)
    {
        var endpoint = new EmbeddingsEndpoint();
        using var http = new HttpClient(endpoint);
        var generator = provider switch
        {
            "OpenAI" => OpenAI(model, http),
            "OpenRouter" => OpenRouter(model, http),
            _ => Azure(model, http),
        };

        var embeddings = await generator.GenerateAsync(["ledger entry"]);
        return (endpoint.Body.ShouldNotBeNull(), embeddings.ShouldHaveSingleItem().Vector.Length);
    }

    [Theory]
    [InlineData("OpenAI", "text-embedding-3-small")]
    [InlineData("OpenAI", "text-embedding-3-large")]
    [InlineData("OpenRouter", "openai/text-embedding-3-small")]
    [InlineData("AzureOpenAI", "text-embedding-3-small")]
    public async Task A_Text_Embedding_3_Model_Is_Asked_For_768_Dimensions(string provider, string model)
    {
        var (body, length) = await Embed(provider, model);

        body.ShouldContain("\"dimensions\":768");
        length.ShouldBe(768);
    }

    [Theory]
    [InlineData("OpenAI", "text-embedding-ada-002")]
    [InlineData("OpenRouter", "openai/text-embedding-ada-002")]
    [InlineData("AzureOpenAI", "my-embeddings")]
    public async Task A_Model_That_Refuses_The_Dimensions_Option_Is_Never_Sent_It(string provider, string model)
    {
        var (body, _) = await Embed(provider, model);

        body.ShouldContain("ledger entry");
        body.ShouldNotContain("dimensions");
    }
}
