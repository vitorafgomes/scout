using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.HuggingFace;

namespace Scout.Llm.Tests.Providers.HuggingFace;

/// <summary>
/// What the Hugging Face provider puts on the wire, read over a recording handler under the real connectors: the URL a
/// chat and an embedding request go to for the default and for a configured endpoint, the key in the Authorization
/// header, the embedding request and the vector it returns, and what the caller gets when the Hugging Face API refuses
/// a call.
/// </summary>
public sealed class HuggingFaceProviderWireTests
{
    private const string ApiKey = "hf-test-key";

    private const string ChatModel = "meta-llama/Llama-3.1-8B-Instruct";

    private const string EmbeddingModel = "sentence-transformers/all-MiniLM-L6-v2";

    private const int OneCallAndTheClientsThreeRetries = 4;

    private const string EmbeddingReply = "[[0.25,-0.5,1.0]]";

    private const string ChatReply =
        """{"id":"c1","object":"chat.completion","created":1,"model":"meta-llama/Llama-3.1-8B-Instruct","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    [Theory]
    [InlineData(
        null,
        "https://api-inference.huggingface.co/v1/chat/completions",
        "https://api-inference.huggingface.co/pipeline/feature-extraction/sentence-transformers/all-MiniLM-L6-v2")]
    [InlineData(
        "https://hf.example.com/v1",
        "https://hf.example.com/v1/v1/chat/completions",
        "https://hf.example.com/v1/pipeline/feature-extraction/sentence-transformers/all-MiniLM-L6-v2")]
    [InlineData(
        "https://hf.example.com/v1/",
        "https://hf.example.com/v1/v1/chat/completions",
        "https://hf.example.com/v1/pipeline/feature-extraction/sentence-transformers/all-MiniLM-L6-v2")]
    [InlineData(
        "https://hf.example.com",
        "https://hf.example.com/v1/chat/completions",
        "https://hf.example.com/pipeline/feature-extraction/sentence-transformers/all-MiniLM-L6-v2")]
    public async Task A_Chat_And_An_Embedding_Request_Go_To_The_Same_Url_For_The_Same_Configured_Endpoint_With_The_Key_As_A_Bearer_Token(
        string? endpoint, string chatUrl, string embeddingsUrl)
    {
        var api = new HuggingFaceApi(_ => Ok(ChatReply));
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
    public async Task An_Embedding_Request_Sends_The_Input_As_Inputs_And_Returns_The_Vector_Hugging_Face_Sent()
    {
        var api = new HuggingFaceApi(_ => Ok(ChatReply));

        var embeddings = await Factory(api).CreateEmbeddingGenerator().GenerateAsync(["hello"]);

        api.Requests.ShouldHaveSingleItem().Body.ShouldBe("""{"inputs":["hello"]}""");
        embeddings.ShouldHaveSingleItem().Vector.ToArray().ShouldBe([0.25f, -0.5f, 1.0f]);
    }

    [Fact]
    public async Task A_Key_Hugging_Face_Rejects_Is_A_Key_Rejected_Failure_That_Never_Carries_The_Key()
    {
        var api = new HuggingFaceApi(_ => Refused(
            HttpStatusCode.Unauthorized, $$"""{"error":"Invalid credentials in Authorization header: {{ApiKey}}"}"""));

        var failure = await Should.ThrowAsync<LlmKeyRejectedException>(
            () => Gateway(api).CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None));

        failure.ToString().ShouldNotContain(ApiKey);
        api.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_Rate_Limit_From_Hugging_Face_Is_Retried_By_The_Connector_And_Then_Is_A_Rate_Limit_Failure()
    {
        var api = new HuggingFaceApi(_ => Refused(
            HttpStatusCode.TooManyRequests, """{"error":"Rate limit reached. You reached free usage limit (reset hourly)."}"""));

        await Should.ThrowAsync<LlmRateLimitException>(
            () => Gateway(api).CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None));

        api.Requests.Count.ShouldBe(OneCallAndTheClientsThreeRetries);
    }

    [Fact]
    public async Task A_Connection_That_Drops_On_A_Chat_Request_Is_Retried_By_The_Connector_And_Then_Reaches_The_Caller_Untranslated()
    {
        var api = new HuggingFaceApi(_ => throw new HttpRequestException("connection reset"));

        var failure = await Should.ThrowAsync<AggregateException>(
            () => Gateway(api).CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None));

        failure.InnerExceptions.ShouldAllBe(inner => inner.InnerException is HttpRequestException);
        failure.InnerExceptions.Count.ShouldBe(OneCallAndTheClientsThreeRetries);
        api.Requests.Count.ShouldBe(OneCallAndTheClientsThreeRetries);
    }

    [Fact]
    public async Task A_Server_Error_On_An_Embedding_Request_Reaches_The_Caller_With_Its_Status_After_One_Request()
    {
        var api = new HuggingFaceApi(
            _ => Ok(ChatReply),
            _ => Refused(HttpStatusCode.InternalServerError, """{"error":"Model is overloaded"}"""));

        var failure = await Should.ThrowAsync<HttpOperationException>(
            () => Factory(api).CreateEmbeddingGenerator().GenerateAsync(["hello"]));

        failure.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        api.Requests.Count.ShouldBe(1);
    }

    private static ChatGateway Gateway(HttpMessageHandler handler)
    {
        var builder = Kernel.CreateBuilder();
        Factory(handler).AddToKernel(builder);
        var kernel = builder.Build();
        return new ChatGateway(
            kernel.GetRequiredService<IChatCompletionService>(),
            kernel,
            Options.Create(new LlmOptions { Provider = "HuggingFace" }));
    }

    private static HuggingFaceLlmProviderFactory Factory(HttpMessageHandler handler, string? endpoint = null) => new(
        Options.Create(new LlmOptions
        {
            Provider = "HuggingFace",
            HuggingFace = new HuggingFaceOptions
            {
                ApiKey = ApiKey,
                Endpoint = endpoint,
                Chat = new ChatModelOptions { Model = ChatModel },
                Embeddings = new EmbeddingModelOptions { Model = EmbeddingModel },
            },
        }),
        new HttpClient(handler));

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Refused(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class HuggingFaceApi(
        Func<HttpRequestMessage, HttpResponseMessage> chat,
        Func<HttpRequestMessage, HttpResponseMessage>? embeddings = null) : HttpMessageHandler
    {
        public List<(string Url, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return request.RequestUri.AbsolutePath.Contains("/pipeline/feature-extraction/", StringComparison.Ordinal)
                ? embeddings?.Invoke(request) ?? Ok(EmbeddingReply)
                : chat(request);
        }
    }
}
