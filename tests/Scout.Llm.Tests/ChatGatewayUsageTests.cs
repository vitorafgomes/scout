using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.AzureAIInference;
using Scout.Llm.Providers.ChatGptPlan;
using Scout.Llm.Providers.Gemini;
using Scout.Llm.Providers.HuggingFace;
using Scout.Llm.Providers.Mistral;
using Scout.Llm.Providers.Ollama;
using Scout.Llm.Providers.OpenAI;
using Scout.Llm.Providers.OpenRouter;
using Scout.Llm.Tests.Providers.ChatGptPlan;
using Scout.Llm.Tests.Providers.Ollama;

namespace Scout.Llm.Tests;

/// <summary>
/// The gateway counts the usage each answer carries, read from the stream of the real connector of each provider over a
/// recording HTTP handler, and reports one usage per completed answer, summed over the model calls of a function round
/// trip; an answer without usage counts nothing. A ChatGPT plan usage limit is reported, and the next answer that
/// completes is reported after it.
/// </summary>
public sealed class ChatGatewayUsageTests : IDisposable
{
    private const string OpenAiUsage = """{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}""";
    private const string PlanUsage =
        """{"input_tokens":10,"input_tokens_details":{"cached_tokens":0},"output_tokens":5,"output_tokens_details":{"reasoning_tokens":0},"total_tokens":15}""";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-usage-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task An_OpenAI_Answer_Asks_For_Usage_On_The_Stream_And_Counts_The_Usage_Chunk()
    {
        var model = new RecordingModel(_ => Sse(OpenAiAnswer("gpt-4o-mini", "Hi", OpenAiUsage)));
        var sink = new RecordingUsageSink();
        var gateway = Gateway("OpenAI", new OpenAILlmProviderFactory(Options(OpenAi()), Client(model, "https://api.openai.com/")), sink);

        var reply = await Ask(gateway);

        reply.ShouldBe("Hi");
        model.Bodies.ShouldHaveSingleItem()["stream_options"]!["include_usage"]!.GetValue<bool>().ShouldBeTrue();
        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("OpenAI", "gpt-4o-mini", 10, 5, 15, null));
    }

    [Fact]
    public async Task An_OpenRouter_Answer_Counts_The_Cost_The_Provider_Reports_In_Its_Usage()
    {
        var model = new RecordingModel(_ => Sse(OpenAiAnswer(
            "openai/gpt-4o-mini", "Hi", """{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"cost":0.0012}""")));
        var sink = new RecordingUsageSink();
        var gateway = Gateway("OpenRouter", new OpenRouterLlmProviderFactory(Options(OpenRouter()), Client(model, "https://openrouter.ai/")), sink);

        await Ask(gateway);

        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("OpenRouter", "openai/gpt-4o-mini", 10, 5, 15, 0.0012m));
    }

    [Fact]
    public async Task An_Ollama_Answer_Counts_The_Usage_Its_Done_Line_Carries()
    {
        var model = new RecordingModel(_ => OllamaApiStub.Lines(OllamaApiStub.TextLines("llama3.2", ["Hi"], prompt: 10, eval: 5)));
        var sink = new RecordingUsageSink();
        var gateway = Gateway("Ollama", new OllamaLlmProviderFactory(Options(Ollama()), Client(model)), sink);

        await Ask(gateway);

        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("Ollama", "llama3.2", 10, 5, 15, null));
    }

    [Fact]
    public async Task An_Ollama_Answer_Whose_Done_Line_Carries_No_Counts_Is_Reported_With_Zero_Tokens()
    {
        var model = new RecordingModel(_ => OllamaApiStub.Lines(OllamaApiStub.TextLines("llama3.2", ["Hi"], prompt: null, eval: null)));
        var sink = new RecordingUsageSink();
        var gateway = Gateway("Ollama", new OllamaLlmProviderFactory(Options(Ollama()), Client(model)), sink);

        await Ask(gateway);

        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("Ollama", "llama3.2", 0, 0, 0, null));
    }

    [Fact]
    public async Task A_Gemini_Answer_Counts_The_Last_Running_UsageMetadata_Of_The_Call_Not_Their_Sum()
    {
        var model = new RecordingModel(_ => Sse(
        [
            """{"candidates":[{"content":{"parts":[{"text":"Hi"}],"role":"model"},"index":0}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":1,"totalTokenCount":11},"modelVersion":"gemini-2.5-flash"}""",
            """{"candidates":[{"content":{"parts":[{"text":" there"}],"role":"model"},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15},"modelVersion":"gemini-2.5-flash"}""",
        ], done: false));
        var sink = new RecordingUsageSink();
        var gateway = Gateway("Gemini", new GeminiLlmProviderFactory(Options(Gemini()), Client(model, "https://generativelanguage.googleapis.com/")), sink);

        var reply = await Ask(gateway);

        reply.ShouldBe("Hi there");
        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("Gemini", "gemini-2.5-flash", 10, 5, 15, null));
    }

    [Fact]
    public async Task A_Gemini_Answer_With_A_Function_Round_Trip_Counts_The_Last_UsageMetadata_Of_Each_Call()
    {
        var model = new RecordingModel(turn => turn == 1
            ? Sse(
            [
                """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"Probe-ping","args":{}}}],"role":"model"},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":20,"candidatesTokenCount":3,"totalTokenCount":23},"modelVersion":"gemini-2.5-flash"}""",
            ], done: false)
            : Sse(
            [
                """{"candidates":[{"content":{"parts":[{"text":"ping says"}],"role":"model"},"index":0}],"usageMetadata":{"promptTokenCount":30,"candidatesTokenCount":2,"totalTokenCount":32},"modelVersion":"gemini-2.5-flash"}""",
                """{"candidates":[{"content":{"parts":[{"text":" pong"}],"role":"model"},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":30,"candidatesTokenCount":4,"totalTokenCount":34},"modelVersion":"gemini-2.5-flash"}""",
            ], done: false));
        var sink = new RecordingUsageSink();
        var gateway = Gateway(
            "Gemini", new GeminiLlmProviderFactory(Options(Gemini()), Client(model, "https://generativelanguage.googleapis.com/")), sink, Probe());

        var reply = await Ask(gateway);

        reply.ShouldBe("ping says pong");
        model.Bodies.Count.ShouldBe(2);
        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("Gemini", "gemini-2.5-flash", 50, 7, 57, null));
    }

    [Fact]
    public async Task A_Mistral_Answer_Counts_The_Usage_On_Its_Last_Chunk()
    {
        var model = new RecordingModel(_ => Sse(
        [
            """{"id":"x","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"role":"assistant","content":"Hi"},"finish_reason":null}]}""",
            """{"id":"x","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"content":" there"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"total_tokens":15,"completion_tokens":5}}""",
        ]));
        var sink = new RecordingUsageSink();
        var gateway = Gateway("Mistral", new MistralLlmProviderFactory(Options(Mistral()), Client(model, "https://api.mistral.ai/")), sink);

        await Ask(gateway);

        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("Mistral", "mistral-small-latest", 10, 5, 15, null));
    }

    [Fact]
    public async Task A_Hugging_Face_Answer_Asks_For_Usage_On_The_Stream_And_Counts_The_Usage_Chunk()
    {
        var model = new RecordingModel(_ => Sse(OpenAiAnswer("meta-llama/Llama-3.1-8B-Instruct", "Hi", OpenAiUsage)));
        var sink = new RecordingUsageSink();
        var gateway = Gateway("HuggingFace", new HuggingFaceLlmProviderFactory(Options(HuggingFace()), Client(model)), sink);

        var reply = await Ask(gateway);

        reply.ShouldBe("Hi");
        model.Bodies.ShouldHaveSingleItem()["stream_options"]!["include_usage"]!.GetValue<bool>().ShouldBeTrue();
        sink.Answers.ShouldHaveSingleItem()
            .ShouldBe(new LlmUsage("HuggingFace", "meta-llama/Llama-3.1-8B-Instruct", 10, 5, 15, null));
    }

    [Fact]
    public async Task An_Azure_AI_Inference_Answer_Counts_The_Usage_Of_Its_Last_Chunk()
    {
        var model = new RecordingModel(_ => Sse(OpenAiAnswer("Phi-3.5", "Hi", OpenAiUsage)));
        var sink = new RecordingUsageSink();
        var gateway = Gateway(
            "AzureAIInference",
            new AzureAIInferenceLlmProviderFactory(Options(AzureAIInference()), Client(model, "https://my-inference.services.ai.azure.com/")),
            sink);

        await Ask(gateway);

        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("AzureAIInference", "Phi-3.5", 10, 5, 15, null));
    }

    [Fact]
    public async Task An_OpenAI_Answer_With_A_Function_Round_Trip_Counts_Both_Model_Calls_Once()
    {
        var model = new RecordingModel(turn => turn == 1
            ? Sse(
            [
                """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"Probe-ping","arguments":"{}"}}]},"finish_reason":null}]}""",
                """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
                """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"gpt-4o-mini","choices":[],"usage":{"prompt_tokens":20,"completion_tokens":3,"total_tokens":23}}""",
            ])
            : Sse(OpenAiAnswer("gpt-4o-mini", "ping says pong", """{"prompt_tokens":30,"completion_tokens":4,"total_tokens":34}""")));
        var sink = new RecordingUsageSink();
        var gateway = Gateway("OpenAI", new OpenAILlmProviderFactory(Options(OpenAi()), Client(model, "https://api.openai.com/")), sink, Probe());

        var reply = await Ask(gateway);

        reply.ShouldBe("ping says pong");
        model.Bodies.Count.ShouldBe(2);
        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("OpenAI", "gpt-4o-mini", 50, 7, 57, null));
    }

    [Fact]
    public async Task A_ChatGpt_Plan_Answer_Counts_The_Usage_Of_The_Responses_Completed_Event()
    {
        var api = new ResponsesApiStub((_, _) => ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("pong", PlanUsage)));
        var sink = new RecordingUsageSink();

        var reply = await Ask(await PlanGateway(api, sink));

        reply.ShouldBe("pong");
        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("ChatGptPlan", "gpt-5.6-luna", 10, 5, 15, null));
    }

    [Fact]
    public async Task A_ChatGpt_Plan_Answer_With_A_Function_Round_Trip_Counts_Each_Completed_Event_Once()
    {
        var api = new ResponsesApiStub((turn, request) => turn == 1
            ? ResponsesApiStub.Sse(ResponsesApiStub.FunctionCallEvents(
                request.Body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>(),
                usage: """{"input_tokens":20,"output_tokens":3,"total_tokens":23}"""))
            : ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("ping says pong", PlanUsage)));
        var sink = new RecordingUsageSink();

        var reply = await Ask(await PlanGateway(api, sink, Probe()));

        reply.ShouldBe("ping says pong");
        api.Requests.Count.ShouldBe(2);
        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("ChatGptPlan", "gpt-5.6-luna", 30, 8, 38, null));
    }

    [Fact]
    public async Task A_ChatGpt_Plan_Usage_Limit_Refused_With_429_Is_Reported_As_The_Plan_Limit_And_Counts_No_Answer()
    {
        var api = new ResponsesApiStub((_, _) => ResponsesApiStub.Error(
            HttpStatusCode.TooManyRequests,
            """{"error":{"message":"limit","type":"usage_limit","param":null,"code":"subscription_sharing_usage_limit_exceeded"}}"""));
        var sink = new RecordingUsageSink();

        await Should.ThrowAsync<LlmQuotaExceededException>(async () => await Ask(await PlanGateway(api, sink)));

        sink.Limits.ShouldBe(["ChatGptPlan"]);
        sink.Answers.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_ChatGpt_Plan_Stream_That_Fails_On_The_Usage_Limit_Is_Reported_And_The_Next_Answer_Is_Reported_After_It()
    {
        var api = new ResponsesApiStub((turn, _) => turn == 1
            ? ResponsesApiStub.Sse(
            [
                """{"type":"response.failed","sequence_number":1,"response":{"id":"resp_1","object":"response","created_at":1700000000,"status":"failed","error":{"code":"subscription_sharing_usage_limit_exceeded","message":"limit"},"model":"gpt-5.6-luna","output":[],"parallel_tool_calls":true,"tool_choice":"auto","tools":[]}}""",
            ])
            : ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("pong", PlanUsage)));
        var sink = new RecordingUsageSink();
        var gateway = await PlanGateway(api, sink);

        await Should.ThrowAsync<LlmQuotaExceededException>(() => Ask(gateway));
        var reply = await Ask(gateway);

        reply.ShouldBe("pong");
        sink.Events.ShouldBe(["limit ChatGptPlan", "answer ChatGptPlan 15"]);
    }

    [Fact]
    public async Task A_Quota_Refusal_That_Is_Not_The_Plan_Usage_Limit_Is_Not_Reported_As_A_Plan_Limit()
    {
        var model = new RecordingModel(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"error":{"message":"no credit","type":"insufficient_quota","param":null,"code":"credit_balance_exhausted"}}""",
                Encoding.UTF8,
                "application/json"),
        });
        var sink = new RecordingUsageSink();
        var gateway = Gateway("OpenAI", new OpenAILlmProviderFactory(Options(OpenAi()), Client(model, "https://api.openai.com/")), sink);

        await Should.ThrowAsync<LlmQuotaExceededException>(() => Ask(gateway));

        sink.Limits.ShouldBeEmpty();
        sink.Answers.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_Agent_Path_Counts_The_Usage_Of_The_Answer_Too()
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Yield(
                new StreamingChatMessageContent(AuthorRole.Assistant, "Hi", modelId: "gpt-4o-mini"),
                new StreamingChatMessageContent(
                    AuthorRole.Assistant,
                    null,
                    modelId: "gpt-4o-mini",
                    metadata: new Dictionary<string, object?>
                    {
                        ["Usage"] = System.ClientModel.Primitives.ModelReaderWriter.Read<OpenAI.Chat.ChatTokenUsage>(BinaryData.FromString(OpenAiUsage)),
                    })));
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chat);
        var kernel = builder.Build();
        var agent = new ChatCompletionAgent { Name = "Project Expert", Instructions = "Ground answers.", Kernel = kernel };
        var sink = new RecordingUsageSink();
        var gateway = new ChatGateway(chat, kernel, Options(OpenAi()), agent: agent, usage: sink);

        var reply = await Ask(gateway);

        reply.ShouldBe("Hi");
        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("OpenAI", "gpt-4o-mini", 10, 5, 15, null));
    }

    [Fact]
    public async Task A_Usage_Sink_That_Fails_Never_Costs_The_Owner_The_Answer()
    {
        var model = new RecordingModel(_ => Sse(OpenAiAnswer("gpt-4o-mini", "Hi", OpenAiUsage)));
        var sink = Substitute.For<ILlmUsageSink>();
        sink.When(s => s.Answered(Arg.Any<LlmUsage>())).Do(_ => throw new IOException("stdout closed"));
        var gateway = Gateway("OpenAI", new OpenAILlmProviderFactory(Options(OpenAi()), Client(model, "https://api.openai.com/")), sink);

        var reply = await Ask(gateway);

        reply.ShouldBe("Hi");
    }

    [Fact]
    public async Task AddScoutLlm_Hands_The_Registered_Usage_Sink_To_The_Gateway()
    {
        var sink = new RecordingUsageSink();
        var services = new ServiceCollection();
        services.AddSingleton<ILlmUsageSink>(sink);
        services.AddScoutLlm(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Llm:Provider"] = "None" })
            .Build());
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IChatGateway>().CompleteAsync(new ChatRequest { UserMessage = "hello", Functions = ["Probe.ping"] }, CancellationToken.None);

        sink.Answers.ShouldHaveSingleItem().Source.ShouldBe("None");
    }

    [Fact]
    public async Task A_Gemini_Answer_Whose_Last_Chunk_Carries_No_Finish_Reason_Still_Counts_Its_Last_Running_UsageMetadata()
    {
        var model = new RecordingModel(_ => Sse(
        [
            """{"candidates":[{"content":{"parts":[{"text":"Hi"}],"role":"model"},"index":0}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":1,"totalTokenCount":11},"modelVersion":"gemini-2.5-flash"}""",
            """{"candidates":[{"content":{"parts":[{"text":" there"}],"role":"model"},"index":0}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15},"modelVersion":"gemini-2.5-flash"}""",
        ], done: false));
        var sink = new RecordingUsageSink();
        var gateway = Gateway("Gemini", new GeminiLlmProviderFactory(Options(Gemini()), Client(model, "https://generativelanguage.googleapis.com/")), sink);

        var reply = await Ask(gateway);

        reply.ShouldBe("Hi there");
        sink.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("Gemini", "gemini-2.5-flash", 10, 5, 15, null));
    }

    [Theory]
    [InlineData(10L, 5L, 15L)]
    [InlineData(10L, null, 10L)]
    [InlineData(null, 5L, 5L)]
    [InlineData(null, null, null)]
    public async Task A_Usage_Without_A_Total_Counts_The_Prompt_Plus_The_Completion_Tokens_And_No_Total_When_Neither_Is_Known(
        long? prompt, long? completion, long? total)
    {
        var usage = await UsageOf(new Dictionary<string, object?>
        {
            ["Usage"] = new Microsoft.Extensions.AI.UsageContent(
                new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = prompt, OutputTokenCount = completion }),
        });

        usage.ShouldBe(new LlmUsage("OpenAI", "gpt-4o-mini", prompt, completion, total, null));
    }

    [Fact]
    public async Task The_Costs_Of_The_Model_Calls_Of_An_Answer_Are_Summed_And_A_Call_Without_A_Cost_Adds_None()
    {
        var usage = await UsageOf(
            new Dictionary<string, object?> { ["Usage"] = ChatUsage("""{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"cost":0.0012}""") },
            new Dictionary<string, object?> { ["Usage"] = ChatUsage("""{"prompt_tokens":20,"completion_tokens":3,"total_tokens":23}""") },
            new Dictionary<string, object?> { ["Usage"] = ChatUsage("""{"prompt_tokens":30,"completion_tokens":4,"total_tokens":34,"cost":0.003}""") });

        usage.ShouldBe(new LlmUsage("OpenAI", "gpt-4o-mini", 60, 12, 72, 0.0042m));
    }

    [Theory]
    [InlineData("no metadata")]
    [InlineData("no Usage key")]
    [InlineData("a null Usage")]
    [InlineData("a Usage of a type no connector reports")]
    public async Task An_Update_Without_A_Usage_Of_A_Known_Shape_Counts_Nothing(string metadata)
    {
        var usage = await UsageOf(metadata switch
        {
            "no metadata" => null,
            "no Usage key" => new Dictionary<string, object?> { ["FinishReason"] = "stop" },
            "a null Usage" => new Dictionary<string, object?> { ["Usage"] = null },
            _ => new Dictionary<string, object?>
            {
                ["Usage"] = new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 },
            },
        });

        usage.ShouldBe(new LlmUsage("OpenAI", "gpt-4o-mini", null, null, null, null));
    }

    private static OpenAI.Chat.ChatTokenUsage ChatUsage(string json)
        => System.ClientModel.Primitives.ModelReaderWriter.Read<OpenAI.Chat.ChatTokenUsage>(BinaryData.FromString(json))!;

    private static async Task<LlmUsage> UsageOf(params Dictionary<string, object?>?[] metadata)
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat.GetStreamingChatMessageContentsAsync(
                Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings?>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Yield(
            [
                new StreamingChatMessageContent(AuthorRole.Assistant, "Hi", modelId: "gpt-4o-mini"),
                .. metadata.Select(entry => new StreamingChatMessageContent(AuthorRole.Assistant, null, modelId: "gpt-4o-mini", metadata: entry)),
            ]));
        var sink = new RecordingUsageSink();

        var reply = await Ask(new ChatGateway(chat, options: Options(OpenAi()), usage: sink));

        reply.ShouldBe("Hi");
        return sink.Answers.ShouldHaveSingleItem();
    }

    private static Task<string> Ask(ChatGateway gateway)
        => gateway.CompleteAsync(new ChatRequest { UserMessage = "hello", Functions = ["Probe.ping"] }, CancellationToken.None);

    private static ChatGateway Gateway(string provider, ILlmProviderFactory factory, ILlmUsageSink sink, KernelPlugin? plugin = null)
    {
        var builder = Kernel.CreateBuilder();
        factory.AddToKernel(builder);
        if (plugin is not null)
        {
            builder.Plugins.Add(plugin);
        }

        var kernel = builder.Build();
        return new ChatGateway(
            kernel.GetRequiredService<IChatCompletionService>(),
            kernel,
            Options(new LlmOptions { Provider = provider }),
            usage: sink);
    }

    private async Task<ChatGateway> PlanGateway(ResponsesApiStub api, ILlmUsageSink sink, KernelPlugin? plugin = null)
    {
        var tokenFile = Path.Combine(_dir, "token.json");
        await File.WriteAllTextAsync(
            tokenFile,
            $$"""{"access_token":"plan-token","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1).UtcDateTime:O}}"}""");
        var options = Options(new LlmOptions { Provider = "ChatGptPlan", ChatGptPlan = new ChatGptPlanOptions { TokenFile = tokenFile } });
        return Gateway("ChatGptPlan", new ChatGptPlanLlmProviderFactory(options, new HttpClient(api)), sink, plugin);
    }

    private static KernelPlugin Probe()
        => KernelPluginFactory.CreateFromFunctions("Probe", [KernelFunctionFactory.CreateFromMethod(() => "pong", "ping")]);

    private static IOptions<LlmOptions> Options(LlmOptions options) => Microsoft.Extensions.Options.Options.Create(options);

    private static LlmOptions OpenAi() => new()
    {
        Provider = "OpenAI",
        OpenAI = new OpenAIOptions
        {
            ApiKey = "test-key",
            Chat = new ChatModelOptions { Model = "gpt-4o-mini" },
            Embeddings = new OpenAIEmbeddingOptions { Model = "text-embedding-3-small" },
        },
    };

    private static LlmOptions OpenRouter() => new()
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
    };

    private static LlmOptions Ollama() => new()
    {
        Provider = "Ollama",
        Ollama = new OllamaOptions
        {
            Endpoint = "http://localhost:11434",
            Chat = new OllamaChatOptions { Model = "llama3.2" },
            Embeddings = new() { Model = "nomic-embed-text" },
        },
    };

    private static LlmOptions Gemini() => new()
    {
        Provider = "Gemini",
        Gemini = new GeminiOptions
        {
            ApiKey = "test-key",
            Chat = new ChatModelOptions { Model = "gemini-2.5-flash" },
            Embeddings = new EmbeddingModelOptions { Model = "text-embedding-004" },
        },
    };

    private static LlmOptions Mistral() => new()
    {
        Provider = "Mistral",
        Mistral = new MistralOptions
        {
            ApiKey = "test-key",
            Chat = new ChatModelOptions { Model = "mistral-small-latest" },
            Embeddings = new EmbeddingModelOptions { Model = "mistral-embed" },
        },
    };

    private static LlmOptions HuggingFace() => new()
    {
        Provider = "HuggingFace",
        HuggingFace = new HuggingFaceOptions
        {
            ApiKey = "test-key",
            Chat = new ChatModelOptions { Model = "meta-llama/Llama-3.1-8B-Instruct" },
            Embeddings = new EmbeddingModelOptions { Model = "sentence-transformers/all-MiniLM-L6-v2" },
        },
    };

    private static LlmOptions AzureAIInference() => new()
    {
        Provider = "AzureAIInference",
        AzureAIInference = new AzureAIInferenceOptions
        {
            ApiKey = "test-key",
            Endpoint = "https://my-inference.services.ai.azure.com/models",
            Chat = new ChatModelOptions { Model = "Phi-3.5" },
            Embeddings = new EmbeddingModelOptions { Model = "text-embedding-3-small" },
        },
    };

    private static HttpClient Client(HttpMessageHandler handler, string? baseAddress = null)
        => baseAddress is null ? new HttpClient(handler) : new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };

    private static string[] OpenAiAnswer(string model, string text, string? usage)
    {
        var start = $$$"""{"id":"c2","object":"chat.completion.chunk","created":1,"model":"{{{model}}}","choices":[{"index":0,"delta":{"role":"assistant","content":{{{JsonValue.Create(text).ToJsonString()}}}},"finish_reason":null}]}""";
        var stop = $$$"""{"id":"c2","object":"chat.completion.chunk","created":1,"model":"{{{model}}}","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""";
        return usage is null
            ? [start, stop]
            : [start, stop, $$$"""{"id":"c2","object":"chat.completion.chunk","created":1,"model":"{{{model}}}","choices":[],"usage":{{{usage}}}}"""];
    }

    private static HttpResponseMessage Sse(IEnumerable<string> chunks, bool done = true) =>
        new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NetworkLikeStream(Encoding.UTF8.GetBytes(
                string.Concat(chunks.Select(chunk => $"data: {chunk}\n\n")) + (done ? "data: [DONE]\n\n" : string.Empty))))
            {
                Headers = { { "Content-Type", "text/event-stream" } },
            },
        };

    private static async IAsyncEnumerable<StreamingChatMessageContent> Yield(
        params StreamingChatMessageContent[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    private sealed class NetworkLikeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class RecordingModel(Func<int, HttpResponseMessage> script) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? []
                : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject());
            return script(Bodies.Count);
        }
    }

    private sealed class RecordingUsageSink : ILlmUsageSink
    {
        public List<LlmUsage> Answers { get; } = [];

        public List<string> Limits { get; } = [];

        public List<string> Events { get; } = [];

        public void Answered(LlmUsage usage)
        {
            Answers.Add(usage);
            Events.Add($"answer {usage.Source} {usage.TotalTokens}");
        }

        public void UsageLimitReached(string source)
        {
            Limits.Add(source);
            Events.Add($"limit {source}");
        }
    }
}
