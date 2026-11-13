using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Mistral;

namespace Scout.Llm.Tests.Providers.Mistral;

/// <summary>
/// What the Mistral provider puts on the wire, read over a recording handler under the real connector: the URL a chat
/// and an embedding request go to for the default and for a configured endpoint, the key in the Authorization header,
/// the embedding request and the vector it returns, and what the gateway does when the Mistral API refuses a call.
/// </summary>
public sealed class MistralProviderWireTests
{
    private const string ApiKey = "mistral-test-key";

    private const int OneCallAndTheClientsThreeRetries = 4;

    private const string EmbeddingReply =
        """{"id":"e1","object":"list","data":[{"object":"embedding","embedding":[0.25,-0.5,1.0],"index":0}],"model":"mistral-embed","usage":{"prompt_tokens":1,"total_tokens":1}}""";

    private const string ChatReply =
        """{"id":"c1","object":"chat.completion","created":1,"model":"mistral-small-latest","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    [Theory]
    [InlineData(null, "https://api.mistral.ai/v1/chat/completions", "https://api.mistral.ai/v1/embeddings")]
    [InlineData("https://mistral.example.com/v1", "https://mistral.example.com/v1/chat/completions", "https://mistral.example.com/v1/embeddings")]
    [InlineData("https://mistral.example.com/v1/", "https://mistral.example.com/v1/chat/completions", "https://mistral.example.com/v1/embeddings")]
    [InlineData("https://mistral.example.com", "https://mistral.example.com/chat/completions", "https://mistral.example.com/embeddings")]
    public async Task A_Chat_And_An_Embedding_Request_Go_To_The_Same_Url_For_The_Same_Configured_Endpoint_With_The_Key_As_A_Bearer_Token(
        string? endpoint, string chatUrl, string embeddingsUrl)
    {
        var api = new MistralApi(_ => Ok(ChatReply));
        var factory = Factory(api, endpoint);
        var builder = Kernel.CreateBuilder();
        factory.AddToKernel(builder);

        await builder.Build().GetRequiredService<IChatCompletionService>()
            .GetChatMessageContentAsync(new ChatHistory("hello"));
        await factory.CreateEmbeddingGenerator().GenerateAsync(["hello"]);

        api.Requests.Select(request => request.Url).ShouldBe([chatUrl, embeddingsUrl]);
        api.Requests.Select(request => request.Authorization).ShouldBe([$"Bearer {ApiKey}", $"Bearer {ApiKey}"]);
    }

    [Fact]
    public async Task An_Embedding_Request_Names_The_Model_And_The_Input_Asks_For_No_Dimensions_And_Returns_The_Vector_Mistral_Sent()
    {
        var api = new MistralApi(_ => Ok(EmbeddingReply));

        var embeddings = await Factory(api).CreateEmbeddingGenerator().GenerateAsync(["hello"]);

        var body = JsonNode.Parse(api.Requests.ShouldHaveSingleItem().Body)!.AsObject();
        string.Join(" ", body.OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(property => $"{property.Key}={property.Value!.ToJsonString()}"))
            .ShouldBe("""encoding_format="base64" input=["hello"] model="mistral-embed" """.TrimEnd());
        embeddings.ShouldHaveSingleItem().Vector.ToArray().ShouldBe([0.25f, -0.5f, 1.0f]);
    }

    [Fact]
    public async Task A_Key_Mistral_Rejects_Is_A_Key_Rejected_Failure_That_Never_Carries_The_Key()
    {
        var api = new MistralApi(_ => Refused(
            HttpStatusCode.Unauthorized, $$"""{"message":"Unauthorized: the key {{ApiKey}} is not valid","request_id":"r1"}"""));

        var failure = await Should.ThrowAsync<LlmKeyRejectedException>(
            () => Gateway(api).CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None));

        failure.ToString().ShouldNotContain(ApiKey);
        api.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_Rate_Limit_From_Mistral_Is_Retried_By_The_Connector_And_Then_Is_A_Rate_Limit_Failure()
    {
        var api = new MistralApi(_ => Refused(
            HttpStatusCode.TooManyRequests,
            """{"object":"error","message":"Requests rate limit exceeded","type":"rate_limited","param":null,"code":"1300"}"""));

        await Should.ThrowAsync<LlmRateLimitException>(
            () => Gateway(api).CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None));

        api.Requests.Count.ShouldBe(OneCallAndTheClientsThreeRetries);
    }

    [Fact]
    public async Task A_Server_Error_On_An_Embedding_Request_Is_Retried_By_The_Connector_And_Then_Reaches_The_Caller_With_Its_Status()
    {
        var api = new MistralApi(
            _ => Ok(ChatReply),
            _ => Refused(HttpStatusCode.InternalServerError, """{"object":"error","message":"Service unavailable","type":"internal_error"}"""));

        var failure = await Should.ThrowAsync<System.ClientModel.ClientResultException>(
            () => Factory(api).CreateEmbeddingGenerator().GenerateAsync(["hello"]));

        failure.Status.ShouldBe(500);
        api.Requests.Count.ShouldBe(OneCallAndTheClientsThreeRetries);
    }

    [Fact]
    public async Task A_Connection_That_Drops_On_A_Chat_Request_Is_Retried_By_The_Connector_And_Then_Reaches_The_Caller_Untranslated()
    {
        var api = new MistralApi(_ => throw new HttpRequestException("connection reset"));

        var failure = await Should.ThrowAsync<AggregateException>(
            () => Gateway(api).CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None));

        failure.InnerExceptions.ShouldAllBe(inner => inner.InnerException is HttpRequestException);
        failure.InnerExceptions.Count.ShouldBe(OneCallAndTheClientsThreeRetries);
        api.Requests.Count.ShouldBe(OneCallAndTheClientsThreeRetries);
    }

    private static ChatGateway Gateway(HttpMessageHandler handler)
    {
        var builder = Kernel.CreateBuilder();
        Factory(handler).AddToKernel(builder);
        var kernel = builder.Build();
        return new ChatGateway(
            kernel.GetRequiredService<IChatCompletionService>(),
            kernel,
            Options.Create(new LlmOptions { Provider = "Mistral" }));
    }

    private static MistralLlmProviderFactory Factory(HttpMessageHandler handler, string? endpoint = null) => new(
        Options.Create(new LlmOptions
        {
            Provider = "Mistral",
            Mistral = new MistralOptions
            {
                ApiKey = ApiKey,
                Endpoint = endpoint,
                Chat = new ChatModelOptions { Model = "mistral-small-latest" },
                Embeddings = new EmbeddingModelOptions { Model = "mistral-embed" },
            },
        }),
        new HttpClient(handler));

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Refused(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class MistralApi(
        Func<HttpRequestMessage, HttpResponseMessage> chat,
        Func<HttpRequestMessage, HttpResponseMessage>? embeddings = null) : HttpMessageHandler
    {
        public List<(string Url, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return request.RequestUri.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal)
                ? embeddings?.Invoke(request) ?? Ok(EmbeddingReply)
                : chat(request);
        }
    }
}
