using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.ChatGptPlan;
using Scout.Llm.Tests.Providers.ChatGptPlan;

namespace Scout.Llm.Tests;

/// <summary>
/// A reply that ends with no text logs one Warning with what the provider reported: the finish reason or status of the
/// stream, the functions the model called and the number of updates; never the prompt, the reply or a credential.
/// </summary>
public sealed class ChatGatewayEmptyReplyLogTests : IDisposable
{
    private const string Question = "audit the whole project against its conventions";
    private const string Token = "plan-access-token-must-never-surface";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-empty-reply-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task An_Empty_Stream_With_A_Finish_Reason_Logs_One_Warning_With_The_Reason()
    {
        var logger = new RecordingLogger();
        var gateway = new ChatGateway(ChatYielding(Update(finishReason: "length")), options: PlanOptions(), logger: logger);

        var reply = await gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None);

        reply.ShouldBeEmpty();
        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldBe(
            "LLM response empty <- provider=ChatGptPlan endpoint=https://api.openai.com/v1/responses model=gpt-5.6-sol (updates=1, finish=length, functionCalls=none).");
        warning.ShouldNotContain(Question);
    }

    [Fact]
    public async Task A_Stream_With_Only_A_Function_Call_Logs_The_Function_Name_And_Never_Its_Arguments()
    {
        var logger = new RecordingLogger();
        var gateway = new ChatGateway(
            ChatYielding(Update(call: new StreamingFunctionCallUpdateContent("call_1", "GitHub-present_audit_findings", """{"verdict":"needs rework"}"""))),
            options: PlanOptions(),
            logger: logger);

        await gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None);

        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("functionCalls=GitHub-present_audit_findings", Case.Sensitive);
        warning.ShouldNotContain("needs rework");
    }

    [Fact]
    public async Task A_Stream_With_Only_A_Function_Call_On_The_Agent_Path_Logs_The_Function_Name()
    {
        var chat = ChatYielding(Update(call: new StreamingFunctionCallUpdateContent("call_1", "GitHub-present_audit_findings", "{}")));
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chat);
        var kernel = builder.Build();
        var agent = new ChatCompletionAgent { Name = "Project Expert", Instructions = "Ground answers.", Kernel = kernel };
        var logger = new RecordingLogger();
        var gateway = new ChatGateway(chat, kernel, PlanOptions(), logger, agent);

        await gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None);

        logger.Warnings.ShouldHaveSingleItem()
            .ShouldContain("functionCalls=GitHub-present_audit_findings", Case.Sensitive);
    }

    [Fact]
    public async Task A_Normal_Stream_Logs_No_Warning()
    {
        var logger = new RecordingLogger();
        var gateway = new ChatGateway(
            ChatYielding(Update(text: "The audit found two issues."), Update(finishReason: "stop")),
            options: PlanOptions(),
            logger: logger);

        var reply = await gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None);

        reply.ShouldBe("The audit found two issues.");
        logger.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(IncompleteEvent, "finish=incomplete (max_output_tokens)")]
    [InlineData(
        """{"type":"response.refusal.done","sequence_number":1,"item_id":"msg_1","output_index":0,"content_index":0,"refusal":"secret-provider-detail"}""",
        "finish=refusal")]
    [InlineData(
        """{"type":"response.completed","sequence_number":1,"response":{"id":"resp_1","object":"response","created_at":1700000000,"status":"completed","model":"gpt-5.6-sol","output":[],"parallel_tool_calls":true,"tool_choice":"auto","tools":[]}}""",
        "finish=Completed")]
    public async Task A_ChatGpt_Plan_Stream_That_Ends_Without_Text_Logs_What_The_Responses_Api_Reported(string terminalEvent, string expected)
    {
        var logger = new RecordingLogger();
        var gateway = await PlanGateway(Answering(terminalEvent), logger);

        var reply = await gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None);

        reply.ShouldBeEmpty();
        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain(expected, Case.Sensitive);
        warning.ShouldNotContain("secret-provider-detail");
        warning.ShouldNotContain(Token);
        warning.ShouldNotContain(Question);
    }

    [Fact]
    public async Task A_ChatGpt_Plan_Stream_That_Called_A_Function_Then_Ended_Without_Text_Logs_The_Function_And_The_Reason()
    {
        var api = FunctionCallThen(IncompleteEvent);
        var logger = new RecordingLogger();
        var gateway = await PlanGateway(api, logger, withFunction: true);

        var reply = await gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None);

        reply.ShouldBeEmpty();
        api.Requests.Count.ShouldBe(2);
        var tool = api.Requests[0].Body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>();
        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain($"finish=incomplete (max_output_tokens), functionCalls={tool})", Case.Sensitive);
    }

    [Theory]
    [InlineData("failed", "subscription_sharing_usage_limit_exceeded")]
    [InlineData("error", "insufficient_quota")]
    public async Task A_ChatGpt_Plan_Stream_That_Fails_On_A_Usage_Limit_Or_Quota_Throws_The_Quota_Exception_With_The_Code(string kind, string code)
    {
        var logger = new RecordingLogger();
        var gateway = await PlanGateway(Answering(Terminal(kind, code)), logger);

        var thrown = await Should.ThrowAsync<LlmQuotaExceededException>(
            () => gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None));

        thrown.Code.ShouldBe(code);
        thrown.Message.ShouldNotContain("secret-provider-detail");
        logger.Warnings.ShouldHaveSingleItem().ShouldContain($"finish={kind} ({code})", Case.Sensitive);
    }

    [Fact]
    public async Task A_ChatGpt_Plan_Stream_That_Fails_On_A_Rate_Limit_Throws_The_Rate_Limit_Exception()
    {
        var gateway = await PlanGateway(Answering(Terminal("failed", "rate_limit_exceeded")), new RecordingLogger());

        var thrown = await Should.ThrowAsync<LlmRateLimitException>(
            () => gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None));

        thrown.Message.ShouldBe("LLM rate limit (rate_limit_exceeded).");
    }

    [Theory]
    [InlineData("failed", "server_error")]
    [InlineData("error", "context_length_exceeded")]
    public async Task A_ChatGpt_Plan_Stream_That_Fails_With_Another_Code_Throws_The_Error_Sentence_With_The_Code(string kind, string code)
    {
        var logger = new RecordingLogger();
        var gateway = await PlanGateway(Answering(Terminal(kind, code)), logger);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None));

        thrown.Message.ShouldBe($"The AI provider ended the answer with an error ({code}).");
        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain($"finish={kind} ({code})", Case.Sensitive);
        warning.ShouldNotContain("secret-provider-detail");
    }

    [Fact]
    public async Task A_ChatGpt_Plan_Stream_That_Called_A_Function_Then_Failed_Throws_The_Error_Sentence_And_Logs_The_Function()
    {
        var api = FunctionCallThen(Terminal("failed", "server_error"));
        var logger = new RecordingLogger();
        var gateway = await PlanGateway(api, logger, withFunction: true);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => gateway.CompleteAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None));

        thrown.Message.ShouldBe("The AI provider ended the answer with an error (server_error).");
        api.Requests.Count.ShouldBe(2);
        var tool = api.Requests[0].Body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>();
        logger.Warnings.ShouldHaveSingleItem().ShouldContain($"finish=failed (server_error), functionCalls={tool})", Case.Sensitive);
    }

    [Fact]
    public async Task In_The_Chat_The_Plan_Usage_Limit_Answers_The_Plan_Sentence()
    {
        var gateway = await PlanGateway(Answering(Terminal("failed", "subscription_sharing_usage_limit_exceeded")), new RecordingLogger());

        var (completed, failed) = await Pumped(gateway);

        completed.ShouldBe(
            "The ChatGPT plan reached its usage limit for apps like Scout; try again later, or choose another AI Source in Scout Desktop.");
        failed.ShouldBeNull();
    }

    [Fact]
    public async Task In_The_Chat_Another_Failure_Code_Takes_The_Failure_Path_With_The_Code()
    {
        var gateway = await PlanGateway(Answering(Terminal("failed", "server_error")), new RecordingLogger());

        var (completed, failed) = await Pumped(gateway);

        completed.ShouldBeNull();
        failed.ShouldBe("The AI provider ended the answer with an error (server_error).");
    }

    [Fact]
    public async Task In_The_Chat_An_Incomplete_Stream_Without_Text_Still_Answers_The_Placeholder()
    {
        var gateway = await PlanGateway(Answering(IncompleteEvent), new RecordingLogger());

        var (completed, failed) = await Pumped(gateway);

        completed.ShouldBe("(no response from model)");
        failed.ShouldBeNull();
    }

    private const string IncompleteEvent =
        """{"type":"response.incomplete","sequence_number":1,"response":{"id":"resp_1","object":"response","created_at":1700000000,"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"model":"gpt-5.6-sol","output":[],"parallel_tool_calls":true,"tool_choice":"auto","tools":[]}}""";

    private static string Terminal(string kind, string code) => kind == "failed"
        ? $$$"""{"type":"response.failed","sequence_number":1,"response":{"id":"resp_1","object":"response","created_at":1700000000,"status":"failed","error":{"code":"{{{code}}}","message":"secret-provider-detail"},"model":"gpt-5.6-sol","output":[],"parallel_tool_calls":true,"tool_choice":"auto","tools":[]}}"""
        : $$"""{"type":"error","sequence_number":1,"code":"{{code}}","message":"secret-provider-detail","param":null}""";

    private static ResponsesApiStub Answering(string terminalEvent)
        => new((_, _) => ResponsesApiStub.Sse([terminalEvent]));

    private static ResponsesApiStub FunctionCallThen(string terminalEvent)
        => new((turn, request) => turn == 1
            ? ResponsesApiStub.Sse(ResponsesApiStub.FunctionCallEvents(request.Body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>()))
            : ResponsesApiStub.Sse([terminalEvent]));

    private async Task<ChatGateway> PlanGateway(ResponsesApiStub api, RecordingLogger logger, bool withFunction = false)
    {
        var tokenFile = Path.Combine(_dir, "token.json");
        await File.WriteAllTextAsync(
            tokenFile,
            $$"""{"access_token":"{{Token}}","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1).UtcDateTime:O}}"}""");
        var options = PlanOptions(tokenFile);
        var builder = Kernel.CreateBuilder();
        new ChatGptPlanLlmProviderFactory(options, new HttpClient(api)).AddToKernel(builder);
        if (withFunction)
        {
            builder.Plugins.Add(KernelPluginFactory.CreateFromFunctions(
                "GitHub", [KernelFunctionFactory.CreateFromMethod(() => "Findings recorded: 0.", "present_audit_findings")]));
        }

        var kernel = builder.Build();
        return new ChatGateway(kernel.GetRequiredService<IChatCompletionService>(), kernel, options, logger);
    }

    private static async Task<(string? Completed, string? Failed)> Pumped(ChatGateway gateway)
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
            gateway.StreamAsync(new ChatRequest { UserMessage = Question }, CancellationToken.None));

        await new StreamPump(new StreamPumpOptions()).RunAsync(response, sink, CancellationToken.None);

        return (completed, failed);
    }

    private static IOptions<LlmOptions> PlanOptions(string tokenFile = "/home/user/.config/scout/chatgpt-token.json")
        => Options.Create(new LlmOptions
        {
            Provider = "ChatGptPlan",
            ChatGptPlan = new ChatGptPlanOptions { TokenFile = tokenFile, Chat = new ChatModelOptions { Model = "gpt-5.6-sol" } },
        });

    private static StreamingChatMessageContent Update(
        string? text = null, string? finishReason = null, StreamingFunctionCallUpdateContent? call = null)
    {
        var update = new StreamingChatMessageContent(
            AuthorRole.Assistant,
            text,
            metadata: finishReason is null ? null : new Dictionary<string, object?> { ["FinishReason"] = finishReason });
        if (call is not null)
        {
            update.Items.Add(call);
        }

        return update;
    }

    private static IChatCompletionService ChatYielding(params StreamingChatMessageContent[] updates)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(updates, CancellationToken.None));
        return chat;
    }

    private static async IAsyncEnumerable<StreamingChatMessageContent> ToAsync(
        IEnumerable<StreamingChatMessageContent> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return update;
        }
    }

    private sealed class RecordingLogger : ILogger<ChatGateway>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Warnings => Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
