using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Anthropic;

namespace Scout.Llm.Tests.Providers.Anthropic;

/// <summary>
/// Why the Claude API refused a request: the API's own error sentence reaches the log, redacted of the key, on one
/// line and capped, and a credit balance refusal is told to the owner as a billing or quota refusal. Every refusal is
/// the Claude API's error JSON under the real Anthropic client, through Semantic Kernel, the gateway and the pump.
/// </summary>
public sealed class AnthropicErrorReasonTests
{
    private const string Key = "sk-ant-api-test-key-must-never-surface";
    private const string Model = "claude-opus-5-5";
    private const string Prefix = "The Anthropic API answered HTTP 400 (invalid_request_error): ";
    private const string Retention = "This organization requires zero data retention, which this model does not offer.";
    private const string CreditBalance = "Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits.";

    [Fact]
    public async Task The_Log_Says_Why_The_Claude_Api_Refused_The_Request()
    {
        var pumpLog = new RecordingLogger<StreamPump>();

        var (completed, failed) = await Pumped(Gateway(Refusing(Retention)), pumpLog);

        completed.ShouldBeNull();
        var warning = pumpLog.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Exception.ShouldBeOfType<HttpOperationException>().Message.ShouldBe(Prefix + Retention);
        failed.ShouldBe(Prefix + Retention);
    }

    [Fact]
    public async Task A_400_Reaches_The_Gateways_Caller_With_The_Apis_Sentence_After_The_Status_And_Type()
    {
        var thrown = await Should.ThrowAsync<HttpOperationException>(() => Ask(Gateway(Refusing(Retention))));

        thrown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        thrown.Message.ShouldBe(Prefix + Retention);
    }

    [Fact]
    public async Task A_Credit_Balance_Refusal_Is_Told_To_The_Owner_As_The_Balance_Running_Out()
    {
        var pumpLog = new RecordingLogger<StreamPump>();

        var (completed, failed) = await Pumped(Gateway(Refusing(CreditBalance)), pumpLog);

        completed.ShouldBe(
            "The AI provider's account has run out of credit. Add credit with the provider, or choose another AI Source in Scout Desktop.");
        failed.ShouldBeNull();
        pumpLog.Entries.ShouldHaveSingleItem().Message.ShouldContain(
            "refused the request for quota or billing (code credit_balance_too_low)", Case.Sensitive);
    }

    [Fact]
    public async Task A_Credit_Balance_Refusal_On_The_Streaming_Path_Is_A_Quota_Refusal_With_Code_credit_balance_too_low()
    {
        var thrown = await Should.ThrowAsync<LlmQuotaExceededException>(() => Ask(Gateway(Refusing(CreditBalance))));

        thrown.Code.ShouldBe("credit_balance_too_low");
    }

    [Fact]
    public async Task A_Credit_Balance_Refusal_On_The_Non_Streaming_Path_Is_A_Quota_Refusal_With_Code_credit_balance_too_low()
    {
        var api = Refusing(CreditBalance);
        var translator = new LiveTranslator(Factory(api).CreateChatCompletionService(Model));

        var thrown = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => translator.TranslateAsync("Bom dia.", [], "en", CancellationToken.None));

        thrown.Code.ShouldBe("credit_balance_too_low");
        ((bool?)api.Requests.ShouldHaveSingleItem().Body["stream"]).ShouldNotBe(true);
    }

    [Fact]
    public async Task The_Sentence_Never_Holds_The_Key_When_The_Api_Echoes_It()
    {
        var thrown = await Should.ThrowAsync<HttpOperationException>(
            () => Ask(Gateway(Refusing($"invalid request for key {Key}, check it"))));

        thrown.Message.ShouldBe(Prefix + "invalid request for key [redacted], check it");
        thrown.ToString().ShouldNotContain(Key);
    }

    [Fact]
    public async Task A_Key_Across_The_Length_Cap_Leaves_No_Part_Of_The_Key_In_The_Sentence()
    {
        var thrown = await Should.ThrowAsync<HttpOperationException>(
            () => Ask(Gateway(Refusing(new string('a', 495) + Key))));

        thrown.Message.ShouldNotContain(Key[..5]);
        thrown.Message.ShouldBe(Prefix + new string('a', 495) + "[reda");
    }

    [Fact]
    public async Task The_Sentence_Is_One_Line()
    {
        var thrown = await Should.ThrowAsync<HttpOperationException>(
            () => Ask(Gateway(Refusing("first line\nsecond\r\n\tline  end"))));

        thrown.Message.ShouldBe(Prefix + "first line second line end");
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    public async Task The_Sentence_Is_Capped_At_500_Characters(int sent, int carried)
    {
        var thrown = await Should.ThrowAsync<HttpOperationException>(
            () => Ask(Gateway(Refusing(new string('b', sent)))));

        thrown.Message.ShouldBe(Prefix + new string('b', carried));
    }

    [Theory]
    [InlineData(400, "invalid_request_error", "enforced_spend_limit_reached", "enforced_spend_limit_reached")]
    [InlineData(403, "permission_error", "billing_frozen", "billing_frozen")]
    [InlineData(403, "permission_error", "organization_on_hold", "organization_on_hold")]
    [InlineData(403, "billing_error", null, "billing_error")]
    [InlineData(402, "billing_error", null, "payment_required")]
    public async Task A_Spend_Limit_A_Frozen_Billing_Or_An_Organisation_On_Hold_Is_A_Quota_Refusal(
        int status, string type, string? errorCode, string quotaCode)
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Error((HttpStatusCode)status, type, "refused", errorCode));

        var thrown = await Should.ThrowAsync<LlmQuotaExceededException>(() => Ask(Gateway(api)));

        thrown.Code.ShouldBe(quotaCode);
    }

    [Fact]
    public async Task An_Unrelated_Details_Code_Stays_A_Generic_Failure()
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Error(
            HttpStatusCode.BadRequest, "invalid_request_error", "refused", "request_body_encoding_invalid"));

        var thrown = await Should.ThrowAsync<HttpOperationException>(() => Ask(Gateway(api)));

        thrown.Message.ShouldBe(Prefix + "refused");
    }

    private static ClaudeApiStub Refusing(string message)
        => new((_, _) => ClaudeApiStub.Error(HttpStatusCode.BadRequest, "invalid_request_error", message));

    private static AnthropicLlmProviderFactory Factory(ClaudeApiStub api)
        => new(Options.Create(Settings()), new HttpClient(api));

    private static LlmOptions Settings() => new()
    {
        Provider = "Anthropic",
        Anthropic = new AnthropicOptions { ApiKey = Key },
    };

    private static ChatGateway Gateway(ClaudeApiStub api)
    {
        var builder = Kernel.CreateBuilder();
        Factory(api).AddToKernel(builder);
        var kernel = builder.Build();
        return new ChatGateway(kernel.GetRequiredService<IChatCompletionService>(), kernel, Options.Create(Settings()));
    }

    private static Task<string> Ask(ChatGateway gateway)
        => gateway.CompleteAsync(new ChatRequest { UserMessage = "Hi" }, CancellationToken.None);

    private static async Task<(string? Completed, string? Failed)> Pumped(ChatGateway gateway, ILogger logger)
    {
        string? completed = null;
        string? failed = null;
        var sink = Substitute.For<IResponseSink>();
        sink.Capabilities.Returns(new RenderCapabilities { MaxBodyLength = 4096 });
        sink.CompleteAsync(Arg.Do<string>(text => completed = text), Arg.Any<IReadOnlyList<Citation>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        sink.FailAsync(Arg.Any<string>(), Arg.Do<string>(reason => failed = reason), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var response = new Conversation.Abstractions.ChatResponse(
            gateway.StreamAsync(new ChatRequest { UserMessage = "Hi" }, CancellationToken.None));

        await new StreamPump(new StreamPumpOptions { Logger = logger }).RunAsync(response, sink, CancellationToken.None);

        return (completed, failed);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
