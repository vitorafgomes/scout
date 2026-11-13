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
/// The gateway translates a provider 429, 402 or 401 into a domain exception so the pump can
/// finalize gracefully: a quota or billing refusal (a 429 naming one, or any 402) becomes
/// <see cref="LlmQuotaExceededException"/>, a throttling 429 becomes
/// <see cref="LlmRateLimitException"/>, a 401 becomes <see cref="LlmKeyRejectedException"/>.
/// Any other provider error propagates unchanged. Covers both the chat and agent streaming paths, through hand-made
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

    private static Kernel RealOpenAIKernel(HttpStatusCode status, string body, string model = "gpt-4o-mini")
    {
        var http = new HttpClient(new CannedResponseHandler(status, body));
        return Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, FakeApiKey, httpClient: http)
            .Build();
    }

    private static ChatGateway RealOpenAIGateway(HttpStatusCode status, string body, string model = "gpt-4o-mini")
    {
        var kernel = RealOpenAIKernel(status, body, model);
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

    private const string PaymentRequiredBody = """
        {"error":{"message":"Insufficient credits. Add more using https://openrouter.ai/settings/credits","code":402}}
        """;

    private const string KeyRejectedBody = """
        {"error":{"message":"Incorrect API key provided: sk-test-****************surface. You can find your API key at https://platform.openai.com/account/api-keys.","type":"invalid_request_error","param":null,"code":"invalid_api_key"}}
        """;

    [Fact]
    public async Task StreamAsync_402_Payment_Required_Is_LlmQuotaExceededException()
    {
        var gateway = RealOpenAIGateway(HttpStatusCode.PaymentRequired, PaymentRequiredBody);

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Code.ShouldBe("payment_required");
        ex.ToString().ShouldNotContain(FakeApiKey);
    }

    [Fact]
    public async Task StreamAsync_402_HttpOperationException_From_Another_Connector_Is_LlmQuotaExceededException()
    {
        var sk = new HttpOperationException(HttpStatusCode.PaymentRequired, PaymentRequiredBody, "payment", null);
        var gateway = new ChatGateway(ChatChunkThenThrow(Chunk("pre"), sk));

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Code.ShouldBe("payment_required");
    }

    [Fact]
    public async Task StreamAsync_401_From_A_Key_Based_Provider_Is_A_Typed_Key_Rejected_Failure()
    {
        var gateway = RealOpenAIGateway(HttpStatusCode.Unauthorized, KeyRejectedBody);

        var ex = await Should.ThrowAsync<LlmKeyRejectedException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Message.ShouldBe("The AI provider rejected its key (HTTP 401).");
        ex.ToString().ShouldNotContain("surface");
    }

    [Fact]
    public async Task StreamAsync_Agent_401_HttpOperationException_Is_A_Typed_Key_Rejected_Failure()
    {
        var sk = new HttpOperationException(HttpStatusCode.Unauthorized, KeyRejectedBody, "unauthorized", null);
        var (gateway, _) = AgentGateway(ChatChunkThenThrow(Chunk("pre"), sk));

        await Should.ThrowAsync<LlmKeyRejectedException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "{}", typeof(LlmQuotaExceededException))]
    [InlineData(HttpStatusCode.Unauthorized, "{}", typeof(LlmKeyRejectedException))]
    [InlineData(HttpStatusCode.NotFound, ModelNotFoundBody, typeof(LlmModelNotFoundException))]
    public void LlmFailures_Translates_402_401_And_A_Missing_Model_Like_The_Gateway(HttpStatusCode status, string body, Type expected)
    {
        var raw = new HttpOperationException(status, body, "provider failure", null);

        var translated = LlmFailures.Translate(raw, "qwen2.5:3b");

        translated.ShouldBeOfType(expected);
        (translated as LlmModelNotFoundException)?.Model.ShouldBe("qwen2.5:3b");
    }

    private const string ModelNotFoundBody = """
        {"error":{"message":"model 'qwen2.5:3b' not found","type":"not_found_error","param":null,"code":null}}
        """;

    [Theory]
    [InlineData(404, ModelNotFoundBody, true)]
    [InlineData(404, """{"error":{"type":"invalid_request_error","code":"model_not_found"}}""", true)]
    [InlineData(404, "404 page not found", false)]
    [InlineData(404, """{"error":{"type":"other"}}""", false)]
    [InlineData(400, """{"error":{"type":"not_found_error"}}""", false)]
    public async Task A_404_For_A_Model_The_Provider_Does_Not_Have_Is_LlmModelNotFoundException_Naming_The_Model(
        int status, string body, bool missingModel)
    {
        var direct = RealOpenAIGateway((HttpStatusCode)status, body, "qwen2.5:3b");
        var (agent, _) = AgentGateway(RealOpenAIKernel((HttpStatusCode)status, body, "qwen2.5:3b").GetRequiredService<IChatCompletionService>());

        foreach (var gateway in new[] { direct, agent })
        {
            var thrown = await Should.ThrowAsync<Exception>(() => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

            if (missingModel)
            {
                thrown.ShouldBeOfType<LlmModelNotFoundException>().Model.ShouldBe("qwen2.5:3b");
            }
            else
            {
                thrown.ShouldBeOfType<System.ClientModel.ClientResultException>().Status.ShouldBe(status);
            }
        }
    }

    [Fact]
    public async Task A_Model_Not_Found_Failure_Never_Carries_The_Providers_Text()
    {
        var gateway = RealOpenAIGateway(HttpStatusCode.NotFound, ModelNotFoundBody, "qwen2.5:3b");

        var ex = await Should.ThrowAsync<LlmModelNotFoundException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        foreach (var text in new[] { ex.Message, ex.Model })
        {
            text.ShouldNotContain("model 'qwen2.5:3b' not found");
            text.ShouldNotContain("not_found_error");
            text.ShouldNotContain("Ollama", Case.Insensitive);
            text.ShouldNotContain("OpenAI", Case.Insensitive);
        }

        ex.Message.ShouldBe("The AI Source does not have the model qwen2.5:3b (HTTP 404).");
    }

    [Fact]
    public async Task StreamAsync_402_Naming_A_Billing_Code_Keeps_That_Code()
    {
        var gateway = RealOpenAIGateway(HttpStatusCode.PaymentRequired, CreditExhaustedBody);

        var ex = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => Drain(gateway.StreamAsync(Request(), CancellationToken.None)));

        ex.Code.ShouldBe("credit_balance_exhausted");
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
