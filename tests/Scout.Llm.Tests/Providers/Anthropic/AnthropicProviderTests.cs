using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Anthropic;

namespace Scout.Llm.Tests.Providers.Anthropic;

/// <summary>
/// The Claude API as an AI Source: the Anthropic provider answers through the official Anthropic client over a fake
/// HTTP handler, reached the way Scout reaches every model, Semantic Kernel and the chat gateway.
/// </summary>
public sealed class AnthropicProviderTests
{
    private const string Key = "sk-ant-api-test-key-must-never-surface";
    private const string Model = "claude-opus-5-5";

    private static AnthropicLlmProviderFactory Factory(HttpMessageHandler handler)
        => new(Options.Create(Settings()), new HttpClient(handler));

    private static LlmOptions Settings() => new()
    {
        Provider = "Anthropic",
        Anthropic = new AnthropicOptions { ApiKey = Key },
    };

    private static ChatGateway Gateway(
        HttpMessageHandler handler,
        ILlmUsageSink? usage = null,
        ILogger<ChatGateway>? logger = null,
        KernelPlugin? plugin = null,
        IFunctionInvocationFilter? filter = null)
    {
        var builder = Kernel.CreateBuilder();
        Factory(handler).AddToKernel(builder);
        if (plugin is not null)
        {
            builder.Plugins.Add(plugin);
        }

        if (filter is not null)
        {
            builder.Services.AddSingleton(filter);
        }

        var kernel = builder.Build();
        return new ChatGateway(
            kernel.GetRequiredService<IChatCompletionService>(), kernel, Options.Create(Settings()), logger, usage: usage);
    }

    private static Task<string> Ask(ChatGateway gateway, double? temperature = null)
        => gateway.CompleteAsync(new ChatRequest { UserMessage = "hello", Temperature = temperature, Functions = ["Probe.ping"] }, CancellationToken.None);

    private static KernelPlugin Probe()
        => KernelPluginFactory.CreateFromFunctions("Probe",
        [
            KernelFunctionFactory.CreateFromMethod(() => "pong", "ping"),
            KernelFunctionFactory.CreateFromMethod(() => "echo", "echo"),
        ]);

    [Fact]
    public async Task The_Request_Goes_To_The_Messages_Api_With_The_Model_Id_And_The_Configured_Key_As_The_Sdk_Sends_It()
    {
        var api = ClaudeApiStub.Answering("Hi");

        await Ask(Gateway(api));

        var request = api.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe(new Uri("https://api.anthropic.com/v1/messages"));
        request.Headers["authorization"].ShouldBe($"Bearer {Key}");
        request.Headers.ShouldNotContainKey("x-api-key");
        request.Headers["anthropic-version"].ShouldBe("2026-10-21");
        request.Body["model"]!.GetValue<string>().ShouldBe(Model);
        ((bool?)request.Body["stream"]).ShouldBe(true);
    }

    [Fact]
    public async Task No_Temperature_Top_P_Or_Top_K_Is_Sent_Even_When_The_Request_Sets_A_Temperature()
    {
        var api = ClaudeApiStub.Answering("Hi");

        await Ask(Gateway(api), temperature: 0.2);

        var body = api.Requests.ShouldHaveSingleItem().Body;
        body.ShouldNotContainKey("temperature");
        body.ShouldNotContainKey("top_p");
        body.ShouldNotContainKey("top_k");
    }

    [Fact]
    public async Task A_Streamed_Reply_Reaches_StreamAsync_As_Text_Chunk_By_Chunk()
    {
        var api = ClaudeApiStub.Answering("Hel", "lo", " there");
        var chunks = new List<string>();

        await foreach (var chunk in Gateway(api).StreamAsync(new ChatRequest { UserMessage = "hello", Functions = ["Probe.ping"] }, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        chunks.ShouldBe(["Hel", "lo", " there"]);
    }

    [Fact]
    public async Task A_Tool_Use_Runs_The_Kernel_Function_Through_The_Kernel_Filters_And_Sends_Its_Tool_Result_Back()
    {
        var api = new ClaudeApiStub((turn, request) => turn == 1
            ? ClaudeApiStub.Sse(ClaudeApiStub.ToolUseEvents(Model, request.Body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>()))
            : ClaudeApiStub.Sse(ClaudeApiStub.TextEvents(Model, ["ping says pong"])));
        var filter = new RecordingFilter();

        var reply = await Ask(Gateway(api, plugin: Probe(), filter: filter));

        reply.ShouldBe("ping says pong");
        filter.Calls.ShouldBe(["Probe.ping"]);
        api.Requests.Count.ShouldBe(2);
        api.Requests[0].Body["tools"].ShouldNotBeNull().AsArray().ShouldHaveSingleItem()!["name"]!.GetValue<string>().ShouldContain("ping");
        var messages = api.Requests[1].Body["messages"]!.AsArray().Select(m => m!.AsObject()).ToList();
        messages.ShouldContain(m => m["role"]!.GetValue<string>() == "assistant"
            && Parts(m).Any(p => p["type"]!.GetValue<string>() == "tool_use" && p["id"]!.GetValue<string>() == "toolu_1"));
        var result = messages.Where(m => m["role"]!.GetValue<string>() == "user").SelectMany(Parts)
            .Where(p => p["type"]!.GetValue<string>() == "tool_result").ShouldHaveSingleItem();
        result["tool_use_id"]!.GetValue<string>().ShouldBe("toolu_1");
        result["content"]!.ToJsonString().ShouldContain("pong");
    }

    [Fact]
    public async Task An_Answers_Input_And_Output_Tokens_Reach_The_Usage_Count_With_Source_Anthropic()
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Sse(ClaudeApiStub.TextEvents(Model, ["Hi"], input: 10, output: 5)));
        var usage = new RecordingUsageSink();

        await Ask(Gateway(api, usage));

        usage.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("Anthropic", Model, 10, 5, 15, null));
    }

    [Fact]
    public async Task A_Function_Round_Trip_Counts_The_Tokens_Of_Both_Claude_Calls_Once()
    {
        var api = new ClaudeApiStub((turn, request) => turn == 1
            ? ClaudeApiStub.Sse(ClaudeApiStub.ToolUseEvents(Model, request.Body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>(), input: 20, output: 3))
            : ClaudeApiStub.Sse(ClaudeApiStub.TextEvents(Model, ["ping says pong"], input: 30, output: 4)));
        var usage = new RecordingUsageSink();

        await Ask(Gateway(api, usage, plugin: Probe()));

        usage.Answers.ShouldHaveSingleItem().ShouldBe(new LlmUsage("Anthropic", Model, 50, 7, 57, null));
    }

    [Fact]
    public async Task A_401_Is_A_Rejected_Key_And_Neither_The_Exception_Nor_The_Log_Carries_The_Key()
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Error(HttpStatusCode.Unauthorized, "authentication_error", $"invalid x-api-key {Key}"));
        var logger = new RecordingLogger();

        var ex = await Should.ThrowAsync<LlmKeyRejectedException>(() => Ask(Gateway(api, logger: logger)));

        ex.ToString().ShouldNotContain(Key);
        logger.Lines.ShouldNotContain(line => line.Contains(Key, StringComparison.Ordinal));
        api.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_429_Is_A_Rate_Limit_After_The_Clients_Two_Retries()
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", $"slow down {Key}"));

        var ex = await Should.ThrowAsync<LlmRateLimitException>(() => Ask(Gateway(api)));

        ex.ToString().ShouldNotContain(Key);
        api.Requests.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(529, "overloaded_error")]
    [InlineData(500, "api_error")]
    public async Task An_Overloaded_Or_Server_Error_Propagates_With_Its_Status_As_Other_Providers_Failures_Do(int status, string type)
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Error((HttpStatusCode)status, type, $"failed for {Key}"));

        var ex = await Should.ThrowAsync<HttpOperationException>(() => Ask(Gateway(api)));

        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        ex.Message.ShouldContain($"({type})");
        ex.ToString().ShouldNotContain(Key);
        api.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Embeddings_Are_Not_Available_And_Nothing_Is_Sent()
    {
        var api = ClaudeApiStub.Answering("unused");

        await Should.ThrowAsync<NotSupportedException>(() => Factory(api).CreateEmbeddingGenerator().GenerateAsync(["text"]));

        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Translation_Model_Is_Named_On_The_Translation_Request_And_The_Chat_Keeps_Its_Model()
    {
        var api = new ClaudeApiStub((_, request) => (bool?)request.Body["stream"] == true
            ? ClaudeApiStub.Sse(ClaudeApiStub.TextEvents(request.Body["model"]!.GetValue<string>(), ["Hi"]))
            : ClaudeApiStub.Message(request.Body["model"]!.GetValue<string>(), "1. Good morning."));
        var services = new ServiceCollection().AddScoutLlm(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Anthropic",
                ["Scout:Llm:Anthropic:ApiKey"] = Key,
                ["Scout:Llm:Translation:Model"] = "claude-haiku-5",
            })
            .Build());
        services.RemoveAll<ILlmProviderFactory>();
        services.AddSingleton<ILlmProviderFactory>(sp => new AnthropicLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(), new HttpClient(api)));
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<ILiveTranslator>().TranslateAsync("Bom dia.", [], "en", CancellationToken.None);
        await provider.GetRequiredService<IChatGateway>().CompleteAsync(new ChatRequest { UserMessage = "hello", Functions = ["Probe.ping"] }, CancellationToken.None);

        api.Requests.Select(r => r.Body["model"]!.GetValue<string>()).ShouldBe(["claude-haiku-5", Model]);
        api.Requests[0].Body.ShouldNotContainKey("tools");
    }

    [Fact]
    public async Task A_Translation_The_Claude_Api_Refuses_With_401_Is_A_Rejected_Key_Without_The_Key()
    {
        var api = new ClaudeApiStub((_, _) => ClaudeApiStub.Error(HttpStatusCode.Unauthorized, "authentication_error", $"invalid x-api-key {Key}"));
        var translator = new LiveTranslator(Factory(api).CreateChatCompletionService(Model));

        var ex = await Should.ThrowAsync<LlmKeyRejectedException>(
            () => translator.TranslateAsync("Bom dia.", [], "en", CancellationToken.None));

        ex.ToString().ShouldNotContain(Key);
        ((bool?)api.Requests.ShouldHaveSingleItem().Body["stream"]).ShouldNotBe(true);
    }

    [Fact]
    public void An_Empty_Key_Is_Refused_When_The_Factory_Is_Built()
    {
        var options = Options.Create(new LlmOptions { Provider = "Anthropic", Anthropic = new AnthropicOptions() });

        Should.Throw<ArgumentException>(() => new AnthropicLlmProviderFactory(options))
            .Message.ShouldContain("AnthropicOptions.ApiKey");
    }

    private static IEnumerable<JsonObject> Parts(JsonObject message)
        => message["content"] is JsonArray parts ? parts.Select(p => p!.AsObject()) : [];

    private sealed class RecordingFilter : IFunctionInvocationFilter
    {
        public List<string> Calls { get; } = [];

        public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            Calls.Add($"{context.Function.PluginName}.{context.Function.Name}");
            await next(context);
        }
    }

    private sealed class RecordingUsageSink : ILlmUsageSink
    {
        public List<LlmUsage> Answers { get; } = [];

        public void Answered(LlmUsage usage) => Answers.Add(usage);

        public void UsageLimitReached(string source)
        {
        }
    }

    private sealed class RecordingLogger : ILogger<ChatGateway>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + " " + exception);
    }
}
