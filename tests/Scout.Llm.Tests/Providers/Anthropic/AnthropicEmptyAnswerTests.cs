using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// A review on the Claude API that ends with no text: the request's output limit, the stop reason the client reads
/// from the stream, and the sentence the owner is given instead of an empty reply. Every stream is the Claude API's
/// server-sent events under the real Anthropic client, through Semantic Kernel and the gateway.
/// </summary>
public sealed class AnthropicEmptyAnswerTests
{
    private const string Key = "sk-ant-api-test-key-must-never-surface";
    private const string Model = "claude-opus-5-5";
    private const string Question = "review https://github.com/octocat/hello/pull/42";
    private const string CutSentence = "The answer was cut off by the model's output limit before any text arrived.";
    private const string FindingsBlock = "```scout-findings\n[]\n```";

    private static readonly string[] ReviewFunctions = ["GitHub.get_pull_request", "GitHub.present_review_findings"];

    [Fact]
    public async Task A_Review_Turn_Asks_Claude_For_64000_Output_Tokens_On_Every_Call_Not_The_Clients_Default_Of_1024()
    {
        var api = new ClaudeApiStub((turn, request) => turn == 1
            ? ClaudeApiStub.Sse(ToolUse(Tool(request, "get_pull_request"), "toolu_1"))
            : ClaudeApiStub.Sse(ClaudeApiStub.TextEvents(Model, ["No findings."])));

        await Review(Gateway(api).Gateway);

        api.Requests.Count.ShouldBe(2);
        api.Requests.Select(r => r.Body["max_tokens"]!.GetValue<int>()).ShouldBe([64_000, 64_000]);
    }

    [Fact]
    public async Task The_Output_Limit_Is_Bound_From_Scout_Llm_Anthropic_Chat_MaxOutputTokens_And_A_Translation_Keeps_Its_400()
    {
        var api = new ClaudeApiStub((_, request) => (bool?)request.Body["stream"] == true
            ? ClaudeApiStub.Sse(ClaudeApiStub.TextEvents(Model, ["Hi"]))
            : ClaudeApiStub.Message(Model, "1. Good morning."));
        var services = new ServiceCollection().AddScoutLlm(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Anthropic",
                ["Scout:Llm:Anthropic:ApiKey"] = Key,
                ["Scout:Llm:Anthropic:Chat:MaxOutputTokens"] = "32000",
            })
            .Build());
        services.RemoveAll<ILlmProviderFactory>();
        services.AddSingleton<ILlmProviderFactory>(sp => new AnthropicLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(), new HttpClient(api)));
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IChatGateway>().CompleteAsync(new ChatRequest { UserMessage = "hello" }, CancellationToken.None);
        await provider.GetRequiredService<ILiveTranslator>().TranslateAsync("Bom dia.", [], "en", CancellationToken.None);

        api.Requests.Select(r => r.Body["max_tokens"]!.GetValue<int>()).ShouldBe([32_000, 400]);
    }

    [Fact]
    public async Task A_Chat_Request_That_Names_800_Tokens_As_The_Conversation_Settings_Do_Sends_800_Over_The_Provider_Default()
    {
        var api = ClaudeApiStub.Answering("Hi");

        await Gateway(api).Gateway.CompleteAsync(
            new ChatRequest { UserMessage = Question, MaxTokens = 800, Functions = ReviewFunctions }, CancellationToken.None);

        api.Requests.ShouldHaveSingleItem().Body["max_tokens"]!.GetValue<int>().ShouldBe(800);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_Output_Limit_That_Is_Not_Positive_Is_Refused_When_The_Factory_Is_Built(int limit)
    {
        var options = Options.Create(new LlmOptions
        {
            Provider = "Anthropic",
            Anthropic = new AnthropicOptions { ApiKey = Key, Chat = new AnthropicChatOptions { MaxOutputTokens = limit } },
        });

        Should.Throw<ArgumentException>(() => new AnthropicLlmProviderFactory(options))
            .Message.ShouldContain("AnthropicOptions.Chat.MaxOutputTokens");
    }

    [Fact]
    public async Task The_Review_That_Gave_No_Answer_Reads_Then_Is_Cut_By_The_Output_Limit_While_Thinking_And_Is_Said_Not_Left_Empty()
    {
        var api = new ClaudeApiStub((turn, request) => turn == 1
            ? ClaudeApiStub.Sse(ToolUse(Tool(request, "get_pull_request"), "toolu_1"))
            : ClaudeApiStub.Sse(Final("max_tokens")));
        var (gateway, logger, _) = Gateway(api);

        var thrown = await Should.ThrowAsync<LlmNoAnswerException>(() => Review(gateway));

        thrown.Message.ShouldBe(CutSentence);
        logger.Warnings.ShouldHaveSingleItem().ShouldBe(
            "LLM response empty <- provider=Anthropic endpoint=https://api.anthropic.com model=claude-opus-5-5 (updates=15, finish=max_tokens, functionCalls=GitHub_get_pull_request).");
        logger.Lines.ShouldNotContain(line => line.Contains(Key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("max_tokens", CutSentence)]
    [InlineData("model_context_window_exceeded", "The answer stopped before any text arrived because the conversation and what Scout read for it filled the model's context window.")]
    [InlineData("refusal", "The model declined to answer this request.")]
    [InlineData("pause_turn", "The model paused its turn before any text arrived.")]
    [InlineData("end_turn", "The model ended its turn without writing an answer.")]
    [InlineData("stop_sequence", "The model ended its turn without writing an answer.")]
    public async Task A_Claude_Stream_That_Ends_With_No_Text_Says_Why_By_Its_Stop_Reason(string stopReason, string sentence)
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Sse(Final(stopReason)));
        var (gateway, logger, _) = Gateway(api);

        var thrown = await Should.ThrowAsync<LlmNoAnswerException>(() => Review(gateway));

        thrown.Message.ShouldBe(sentence);
        logger.Warnings.ShouldHaveSingleItem().ShouldContain($"finish={stopReason},", Case.Sensitive);
    }

    [Fact]
    public async Task In_The_Chat_An_Answer_Cut_By_The_Limit_Shows_The_Sentence_And_Never_An_Empty_Reply()
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Sse(Final("max_tokens")));

        var (completed, failed) = await Pumped(Gateway(api).Gateway);

        completed.ShouldBe(CutSentence);
        failed.ShouldBeNull();
    }

    [Fact]
    public async Task A_Plain_Chat_Answer_At_800_Tokens_Cut_While_Thinking_Shows_The_Sentence()
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Sse(Final("max_tokens")));
        var gateway = Gateway(api).Gateway;

        var (completed, _) = await Pumped(gateway, new ChatRequest { UserMessage = "why is the build red?", MaxTokens = 800 });

        api.Requests.ShouldHaveSingleItem().Body["max_tokens"]!.GetValue<int>().ShouldBe(800);
        completed.ShouldBe(CutSentence);
    }

    [Fact]
    public async Task A_Call_With_Settings_That_Name_No_Limit_As_An_Audit_Batch_Sends_Asks_For_64000_Tokens()
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Message(Model, "Findings recorded."));
        var chat = new AnthropicLlmProviderFactory(Options.Create(Settings()), new HttpClient(api)).CreateChatCompletionService(Model);

        await chat.GetChatMessageContentAsync(new ChatHistory("audit the docs dimension"), new PromptExecutionSettings());

        api.Requests.ShouldHaveSingleItem().Body["max_tokens"]!.GetValue<int>().ShouldBe(64_000);
    }

    [Fact]
    public async Task An_Answer_Whose_Text_Arrived_Before_The_Limit_Cut_It_Keeps_Its_Text_And_Is_Not_A_Failure()
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Sse(
        [
            .. ClaudeApiStub.TextEvents(Model, ["The first finding is"]).SkipLast(2),
            End("max_tokens"),
            """{"type":"message_stop"}""",
        ]));

        var reply = await Review(Gateway(api).Gateway);

        reply.ShouldBe("The first finding is");
    }

    [Fact]
    public async Task A_Findings_Call_Cut_By_The_Limit_Never_Runs_Its_Truncated_Input_And_The_Turn_Ends_On_The_Next_Calls_Stop_Reason()
    {
        var api = new ClaudeApiStub((turn, request) => turn switch
        {
            1 => ClaudeApiStub.Sse(ToolUse(Tool(request, "get_pull_request"), "toolu_1")),
            2 => ClaudeApiStub.Sse(CutToolUse(Tool(request, "present_review_findings"))),
            _ => ClaudeApiStub.Sse(Final("max_tokens")),
        });
        var (gateway, logger, findings) = Gateway(api);

        var thrown = await Should.ThrowAsync<LlmNoAnswerException>(() => Review(gateway));

        thrown.Message.ShouldBe(CutSentence);
        findings.Calls.ShouldBe(0);
        api.Requests.Count.ShouldBe(3);
        var result = api.Requests[2].Body["messages"]!.AsArray().SelectMany(Parts)
            .Single(p => p["type"]!.GetValue<string>() == "tool_result" && p["tool_use_id"]!.GetValue<string>() == "toolu_9");
        ((bool?)result["is_error"]).ShouldBe(true);
        logger.Warnings.ShouldHaveSingleItem().ShouldContain("finish=max_tokens, functionCalls=GitHub_get_pull_request,GitHub_present_review_findings)", Case.Sensitive);
    }

    [Fact]
    public async Task A_Review_Whose_Findings_Were_Presented_Before_A_Silent_End_Answers_The_Findings_Card_Not_A_Failure()
    {
        var api = new ClaudeApiStub((turn, request) => turn == 1
            ? ClaudeApiStub.Sse(ToolUse(Tool(request, "present_review_findings"), "toolu_1", """{\"findings\":\"[]\"}"""))
            : ClaudeApiStub.Sse(Final("end_turn")));
        var (gateway, _, findings) = Gateway(api);

        using var turn = TurnAttachments.Begin();
        var (completed, failed) = await Pumped(gateway);

        findings.Calls.ShouldBe(1);
        completed.ShouldBe(FindingsBlock);
        failed.ShouldBeNull();
    }

    [Fact]
    public async Task At_The_Function_Call_Cap_The_Last_Call_Offers_No_Tools_And_A_Silent_End_There_Is_Said()
    {
        var api = new ClaudeApiStub((turn, request) => request.Body["tools"] is null
            ? ClaudeApiStub.Sse(Final("end_turn"))
            : ClaudeApiStub.Sse(ToolUse(Tool(request, "get_pull_request"), $"toolu_{turn}")));

        var thrown = await Should.ThrowAsync<LlmNoAnswerException>(() => Review(Gateway(api).Gateway));

        thrown.Message.ShouldBe("The model ended its turn without writing an answer.");
        api.Requests.Count.ShouldBe(129);
        api.Requests.Take(128).ShouldAllBe(r => r.Body["tools"] != null);
        api.Requests[^1].Body.ShouldNotContainKey("tools");
    }

    [Fact]
    public async Task A_Stream_Whose_Last_Stop_Reason_Is_tool_use_With_No_Text_Says_The_Model_Was_Still_Calling_Functions()
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Yield(new StreamingChatMessageContent(
                AuthorRole.Assistant, null, metadata: new Dictionary<string, object?> { ["FinishReason"] = "tool_use" })));
        var gateway = new ChatGateway(chat, options: Options.Create(Settings()));

        var thrown = await Should.ThrowAsync<LlmNoAnswerException>(() => Review(gateway));

        thrown.Message.ShouldBe("The model was still asking to call functions when the answer ended, so no text arrived.");
    }

    [Fact]
    public async Task A_Very_Large_Tool_Result_Reaches_The_Next_Claude_Request_Whole()
    {
        var diff = new string('d', 1_000_000);
        var api = new ClaudeApiStub((turn, request) => turn == 1
            ? ClaudeApiStub.Sse(ToolUse(Tool(request, "get_pull_request"), "toolu_1"))
            : ClaudeApiStub.Sse(ClaudeApiStub.TextEvents(Model, ["No findings."])));

        var reply = await Review(Gateway(api, pullRequest: diff).Gateway);

        reply.ShouldBe("No findings.");
        var result = api.Requests[1].Body["messages"]!.AsArray().SelectMany(Parts)
            .Single(p => p["type"]!.GetValue<string>() == "tool_result");
        result["content"]!.GetValue<string>().ShouldBe(diff);
    }

    [Fact]
    public async Task A_400_For_A_Prompt_Over_The_Context_Window_Is_Sent_Once_And_Thrown_With_The_Apis_Sentence_Without_The_Key()
    {
        var api = PromptTooLong();

        var thrown = await Should.ThrowAsync<HttpOperationException>(() => Review(Gateway(api).Gateway));

        thrown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        thrown.Message.ShouldBe("The Anthropic API answered HTTP 400 (invalid_request_error): prompt is too long: 1048577 tokens > 1048576 maximum [redacted]");
        thrown.ToString().ShouldNotContain(Key);
        api.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task In_The_Chat_A_400_For_A_Prompt_Over_The_Context_Window_Takes_The_Failure_Path()
    {
        var (completed, failed) = await Pumped(Gateway(PromptTooLong()).Gateway);

        completed.ShouldBeNull();
        failed.ShouldBe("The Anthropic API answered HTTP 400 (invalid_request_error): prompt is too long: 1048577 tokens > 1048576 maximum [redacted]");
    }

    private static ClaudeApiStub PromptTooLong()
        => new((_, _) => ClaudeApiStub.Error(
            HttpStatusCode.BadRequest, "invalid_request_error", $"prompt is too long: 1048577 tokens > 1048576 maximum {Key}"));

    private static Task<string> Review(ChatGateway gateway)
        => gateway.CompleteAsync(new ChatRequest { UserMessage = Question, Functions = ReviewFunctions }, CancellationToken.None);

    private static async Task<(string? Completed, string? Failed)> Pumped(ChatGateway gateway, ChatRequest? request = null)
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
            gateway.StreamAsync(request ?? new ChatRequest { UserMessage = Question, Functions = ReviewFunctions }, CancellationToken.None));

        await new StreamPump(new StreamPumpOptions()).RunAsync(response, sink, CancellationToken.None);

        return (completed, failed);
    }

    private static LlmOptions Settings() => new()
    {
        Provider = "Anthropic",
        Anthropic = new AnthropicOptions { ApiKey = Key },
    };

    private static (ChatGateway Gateway, RecordingLogger Logger, FindingsCounter Findings) Gateway(ClaudeApiStub api, string pullRequest = "pull request 42")
    {
        var options = Options.Create(Settings());
        var counter = new FindingsCounter();
        var builder = Kernel.CreateBuilder();
        new AnthropicLlmProviderFactory(options, new HttpClient(api)).AddToKernel(builder);
        builder.Plugins.Add(KernelPluginFactory.CreateFromFunctions("GitHub",
        [
            KernelFunctionFactory.CreateFromMethod(() => pullRequest, "get_pull_request"),
            KernelFunctionFactory.CreateFromMethod(
                (string findings) =>
                {
                    counter.Calls++;
                    TurnAttachments.Attach(FindingsBlock);
                    return "Findings recorded.";
                },
                "present_review_findings"),
        ]));
        var kernel = builder.Build();
        var logger = new RecordingLogger();
        return (new ChatGateway(kernel.GetRequiredService<IChatCompletionService>(), kernel, options, logger), logger, counter);
    }

    private static string Tool(ClaudeRequest request, string function)
        => request.Body["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Single(n => n.EndsWith(function, StringComparison.Ordinal));

    private static IEnumerable<JsonObject> Parts(JsonNode? message)
        => message!["content"] is JsonArray parts ? parts.Select(p => p!.AsObject()) : [];

    private static IEnumerable<string> ToolUse(string name, string id, string input = "{}") =>
    [
        Start(),
        $$$$"""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"{{{{id}}}}","name":"{{{{name}}}}","input":{}}}""",
        $$$"""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{{{input}}}"}}""",
        """{"type":"content_block_stop","index":0}""",
        End("tool_use"),
        """{"type":"message_stop"}""",
    ];

    private static IEnumerable<string> CutToolUse(string name) =>
    [
        Start(),
        $$$$"""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_9","name":"{{{{name}}}}","input":{}}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"findings\":\"[{\\\"severity\\\":\\\"high\\\",\\\"title\\\":\\\"Nul"}}""",
        """{"type":"content_block_stop","index":0}""",
        End("max_tokens"),
        """{"type":"message_stop"}""",
    ];

    private static IEnumerable<string> Final(string stopReason) =>
    [
        Start(),
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":""}}""",
        """{"type":"content_block_stop","index":0}""",
        End(stopReason),
        """{"type":"message_stop"}""",
    ];

    private static string Start() => new JsonObject
    {
        ["type"] = "message_start",
        ["message"] = new JsonObject
        {
            ["id"] = "msg_1",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = Model,
            ["content"] = new JsonArray(),
            ["stop_reason"] = null,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 1 },
        },
    }.ToJsonString();

    private static string End(string stopReason) => new JsonObject
    {
        ["type"] = "message_delta",
        ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null },
        ["usage"] = new JsonObject { ["output_tokens"] = 1024 },
    }.ToJsonString();

    private static async IAsyncEnumerable<StreamingChatMessageContent> Yield(
        StreamingChatMessageContent update, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return update;
    }

    private sealed class FindingsCounter
    {
        public int Calls { get; set; }
    }

    private sealed class RecordingLogger : ILogger<ChatGateway>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Lines => Entries.Select(entry => entry.Message);

        public IEnumerable<string> Warnings => Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
    }
}
