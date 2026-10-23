using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

/// <summary>
/// The gateway translates a provider 429 into a domain exception so the pump can
/// finalize gracefully: a quota or billing refusal becomes
/// <see cref="LlmQuotaExceededException"/>, a throttling 429 becomes
/// <see cref="LlmRateLimitException"/>. Any other provider error propagates
/// unchanged. Covers both the chat and agent streaming paths, through hand-made
/// Semantic Kernel exceptions and through the real OpenAI connector.
/// </summary>
public sealed class ChatGatewayRateLimitTests
{
    private static StreamingChatMessageContent Chunk(string? content)
        => new(AuthorRole.Assistant, content);

    private static async IAsyncEnumerable<StreamingChatMessageContent> ChunkThenThrow(
        StreamingChatMessageContent chunk,
        Exception toThrow,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return chunk;
        throw toThrow;
    }

    private static IChatCompletionService ChatChunkThenThrow(
        StreamingChatMessageContent chunk, Exception toThrow)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ChunkThenThrow(chunk, toThrow, CancellationToken.None));
        return chat;
    }

    private static (ChatGateway gateway, Kernel kernel) AgentGateway(IChatCompletionService chat)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chat);
        var kernel = builder.Build();
        var agent = new ChatCompletionAgent
        {
            Name = "Project Expert",
            Instructions = "Ground answers in project context.",
            Kernel = kernel,
        };
        return (new ChatGateway(chat, kernel, agent: agent), kernel);
    }

    private static ChatRequest Request() => new() { UserMessage = "hello" };

    private static async Task<List<string>> Drain(IAsyncEnumerable<string> stream)
    {
        var delivered = new List<string>();
        await foreach (var chunk in stream)
        {
            delivered.Add(chunk);
        }

        return delivered;
    }

    [Fact]
    public async Task StreamAsync_Translates_429_HttpOperationException_To_LlmRateLimitException()
    {
        var sk = new HttpOperationException(
            HttpStatusCode.TooManyRequests, "rate", "body", null);
        var chat = ChatChunkThenThrow(Chunk("pre"), sk);
        var gateway = new ChatGateway(chat);

        var delivered = new List<string>();
        var ex = await Should.ThrowAsync<LlmRateLimitException>(async () =>
        {
            await foreach (var chunk in gateway.StreamAsync(Request(), CancellationToken.None))
            {
                delivered.Add(chunk);
            }
        });

        ex.ShouldBeOfType<LlmRateLimitException>();
        ex.InnerException.ShouldBeOfType<HttpOperationException>();
        // The chunk emitted before the 429 still reached the consumer.
        delivered.ShouldBe(["pre"]);
    }

    [Fact]
    public async Task StreamAsync_NonRateLimit_HttpOperationException_Propagates_Unchanged()
    {
        var sk = new HttpOperationException(
            HttpStatusCode.InternalServerError, "boom", "body", null);
        var chat = ChatChunkThenThrow(Chunk("pre"), sk);
        var gateway = new ChatGateway(chat);

        // 500 is not a rate limit: the raw SK exception must surface untranslated.
        await Should.ThrowAsync<HttpOperationException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));
    }

    [Fact]
    public async Task StreamAsync_Agent_Translates_429_HttpOperationException_To_LlmRateLimitException()
    {
        var sk = new HttpOperationException(
            HttpStatusCode.TooManyRequests, "rate", "body", null);
        var chat = ChatChunkThenThrow(Chunk("pre"), sk);
        var (gateway, _) = AgentGateway(chat);

        var ex = await Should.ThrowAsync<LlmRateLimitException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.InnerException.ShouldBeOfType<HttpOperationException>();
    }

    private const string FakeApiKey = "sk-test-key-must-never-surface";

    private const string CreditExhaustedBody = """
        {"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota","param":null,"code":"credit_balance_exhausted"}}
        """;

    private const string ThrottlingBody = """
        {"error":{"message":"Rate limit reached for gpt-4o-mini in organization org-test on requests per min (RPM): Limit 3, Used 3, Requested 1.","type":"requests","param":null,"code":"rate_limit_exceeded"}}
        """;

    private static Kernel RealOpenAIKernel(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new CannedResponseHandler(status, body));
        return Kernel.CreateBuilder()
            .AddOpenAIChatCompletion("gpt-4o-mini", FakeApiKey, httpClient: http)
            .Build();
    }

    private static ChatGateway RealOpenAIGateway(HttpStatusCode status, string body)
    {
        var kernel = RealOpenAIKernel(status, body);
        return new ChatGateway(kernel.GetRequiredService<IChatCompletionService>(), kernel);
    }

    [Fact]
    public async Task StreamAsync_Account_Without_Credit_Through_Real_OpenAI_Connector_Is_LlmQuotaExceededException()
    {
        var gateway = RealOpenAIGateway(HttpStatusCode.TooManyRequests, CreditExhaustedBody);

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Code.ShouldBe("credit_balance_exhausted");
    }

    [Fact]
    public async Task StreamAsync_Agent_Account_Without_Credit_Through_Real_OpenAI_Connector_Is_LlmQuotaExceededException()
    {
        var (gateway, _) = AgentGateway(RealOpenAIKernel(
            HttpStatusCode.TooManyRequests, CreditExhaustedBody).GetRequiredService<IChatCompletionService>());

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Code.ShouldBe("credit_balance_exhausted");
    }

    [Fact]
    public async Task StreamAsync_Throttling_429_Through_Real_OpenAI_Connector_Is_LlmRateLimitException()
    {
        var gateway = RealOpenAIGateway(HttpStatusCode.TooManyRequests, ThrottlingBody);

        await Should.ThrowAsync<LlmRateLimitException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));
    }

    [Fact]
    public async Task StreamAsync_Quota_Exception_Never_Carries_The_Api_Key()
    {
        var gateway = RealOpenAIGateway(HttpStatusCode.TooManyRequests, CreditExhaustedBody);

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Message.ShouldNotContain(FakeApiKey);
        ex.ToString().ShouldNotContain(FakeApiKey);
    }

    [Fact]
    public async Task StreamAsync_429_HttpOperationException_With_Quota_Body_Is_LlmQuotaExceededException()
    {
        var sk = new HttpOperationException(
            HttpStatusCode.TooManyRequests, CreditExhaustedBody, "quota", null);
        var gateway = new ChatGateway(ChatChunkThenThrow(Chunk("pre"), sk));

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Code.ShouldBe("credit_balance_exhausted");
    }

    [Theory]
    [InlineData("credit_balance_exhausted")]
    [InlineData("organization_spend_limit_exceeded")]
    [InlineData("project_spend_limit_exceeded")]
    [InlineData("organization_usage_limit_exceeded")]
    public async Task StreamAsync_Billing_Code_Through_Real_OpenAI_Connector_Is_LlmQuotaExceededException_With_That_Code(
        string code)
    {
        var body = $$$"""
            {"error":{"message":"Billing limit reached.","type":"requests","param":null,"code":"{{{code}}}"}}
            """;
        var gateway = RealOpenAIGateway(HttpStatusCode.TooManyRequests, body);

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Code.ShouldBe(code);
    }

    [Theory]
    [InlineData("\"insufficient_quota\"", "insufficient_quota")]
    [InlineData("null", "insufficient_quota")]
    public async Task StreamAsync_Insufficient_Quota_Type_Through_Real_OpenAI_Connector_Is_LlmQuotaExceededException(
        string codeJson, string expectedCode)
    {
        var body = $$$"""
            {"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota","param":null,"code":{{{codeJson}}}}}
            """;
        var gateway = RealOpenAIGateway(HttpStatusCode.TooManyRequests, body);

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Code.ShouldBe(expectedCode);
    }

    private sealed class CannedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
