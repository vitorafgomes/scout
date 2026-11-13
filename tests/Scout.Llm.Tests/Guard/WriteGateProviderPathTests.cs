using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.ChatGptPlan;
using Scout.Llm.Providers.Gemini;
using Scout.Llm.Tests.Providers.ChatGptPlan;

namespace Scout.Llm.Tests.Guard;

public sealed class WriteGateProviderPathTests : IDisposable
{
    private const string QueuedText =
        "Queued for the owner's approval: an Approve card is waiting in the chat and in Approvals of Scout Desktop (action act-7).";

    private readonly string _dir = Directory.CreateTempSubdirectory("scout-write-gate-paths-").FullName;

    private bool _publishRan;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task On_The_Gemini_Path_A_Model_Initiated_Call_To_A_Declared_Write_Is_Gated_And_The_Model_Is_Told_It_Is_Queued()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-7");
        var model = new GeminiScript();
        var services = Compose(gate, new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "Gemini",
            ["Scout:Llm:Gemini:ApiKey"] = "gemini-test-key",
            ["Scout:Llm:Gemini:Chat:Model"] = "gemini-2.5-flash",
            ["Scout:Llm:Gemini:Embeddings:Model"] = "text-embedding-004",
        });
        services.AddSingleton<ILlmProviderFactory>(sp => new GeminiLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(), new HttpClient(model)));
        using var provider = services.BuildServiceProvider();

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video", Functions = ["Studio.publish"] }, CancellationToken.None);

        var geminiTools = model.Bodies[0]["tools"].ShouldNotBeNull().ToJsonString();
        geminiTools.ShouldContain("publish");
        geminiTools.ShouldNotContain("archive");
        _publishRan.ShouldBeFalse();
        gate.Requests.ShouldHaveSingleItem().ShouldBe(
            ("youtube", "videos.insert", "Studio.publish", "publish(title=Launch day)"));
        model.Bodies.Count.ShouldBe(2);
        model.Bodies[1]["contents"]!.AsArray()
            .SelectMany(content => content!["parts"]!.AsArray())
            .Select(part => part!["functionResponse"]?["response"]?["content"]?.GetValue<string>())
            .ShouldContain(QueuedText);
        reply.ShouldBe("It waits for your approval.");
    }

    [Fact]
    public async Task On_The_ChatGpt_Plan_Path_A_Model_Initiated_Call_To_A_Declared_Write_Is_Gated_And_The_Model_Is_Told_It_Is_Queued()
    {
        var tokenFile = Path.Combine(_dir, "token.json");
        await File.WriteAllTextAsync(
            tokenFile,
            $$"""{"access_token":"plan-token","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1).UtcDateTime:O}}"}""");
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-7");
        var api = new ResponsesApiStub((turn, request) => turn == 1
            ? ResponsesApiStub.Sse(ResponsesApiStub.FunctionCallEvents(
                request.Body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>(),
                """{"title":"Launch day"}"""))
            : ResponsesApiStub.Sse(ResponsesApiStub.TextEvents("It waits for your approval.")));
        var services = Compose(gate, new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "ChatGptPlan",
            ["Scout:Llm:ChatGptPlan:TokenFile"] = tokenFile,
        });
        services.AddSingleton<ILlmProviderFactory>(sp => new ChatGptPlanLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(), new HttpClient(api)));
        using var provider = services.BuildServiceProvider();

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video", Functions = ["Studio.publish"] }, CancellationToken.None);

        var planTools = api.Requests[0].Body["tools"].ShouldNotBeNull().AsArray();
        planTools.Count.ShouldBe(1);
        planTools.ToJsonString().ShouldContain("publish");
        _publishRan.ShouldBeFalse();
        gate.Requests.ShouldHaveSingleItem().ShouldBe(
            ("youtube", "videos.insert", "Studio.publish", "publish(title=Launch day)"));
        api.Requests.Count.ShouldBe(2);
        api.Requests[1].Body["input"]!.AsArray()
            .Single(item => item!["type"]?.GetValue<string>() == "function_call_output")!["output"]!
            .GetValue<string>().ShouldContain(QueuedText);
        reply.ShouldBe("It waits for your approval.");
    }

    [Fact]
    public async Task On_The_Azure_OpenAI_Path_A_Model_Initiated_Call_To_A_Declared_Write_Is_Gated_And_The_Model_Is_Told_It_Is_Queued()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-7");
        var model = new ChatCompletionsScript("gpt-4o");
        var services = Compose(gate, new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "AzureOpenAI",
            ["Scout:Llm:AzureOpenAI:ApiKey"] = "test-key",
            ["Scout:Llm:AzureOpenAI:Endpoint"] = "https://my-resource.openai.azure.com",
            ["Scout:Llm:AzureOpenAI:Chat:Deployment"] = "gpt-4o",
            ["Scout:Llm:AzureOpenAI:Embeddings:Deployment"] = "text-embedding-3-small",
        });
        services.AddSingleton<ILlmProviderFactory>(sp => new Scout.Llm.Providers.AzureOpenAI.AzureOpenAILlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(),
            new HttpClient(model) { BaseAddress = new Uri("https://my-resource.openai.azure.com/") }));
        using var provider = services.BuildServiceProvider();

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video", Functions = ["Studio.publish"] }, CancellationToken.None);

        var tools = model.Bodies[0]["tools"].ShouldNotBeNull().ToJsonString();
        tools.ShouldContain("publish");
        tools.ShouldNotContain("archive");
        _publishRan.ShouldBeFalse();
        gate.Requests.ShouldHaveSingleItem().ShouldBe(
            ("youtube", "videos.insert", "Studio.publish", "publish(title=Launch day)"));
        model.Bodies.Count.ShouldBe(2);
        model.Bodies[1]["messages"]!.AsArray()
            .Single(message => message!["role"]?.GetValue<string>() == "tool")!["content"]!
            .GetValue<string>().ShouldContain(QueuedText);
        reply.ShouldBe("It waits for your approval.");
    }

    [Fact]
    public async Task On_The_Mistral_Path_A_Model_Initiated_Call_To_A_Declared_Write_Is_Gated_And_The_Model_Is_Told_It_Is_Queued()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-7");
        var model = new ChatCompletionsScript("mistral-small-latest");
        var services = Compose(gate, new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "Mistral",
            ["Scout:Llm:Mistral:ApiKey"] = "test-key",
            ["Scout:Llm:Mistral:Chat:Model"] = "mistral-small-latest",
            ["Scout:Llm:Mistral:Embeddings:Model"] = "mistral-embed",
        });
        services.AddSingleton<ILlmProviderFactory>(sp => new Scout.Llm.Providers.Mistral.MistralLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(), new HttpClient(model)));
        using var provider = services.BuildServiceProvider();

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video", Functions = ["Studio.publish"] }, CancellationToken.None);

        var tools = model.Bodies[0]["tools"].ShouldNotBeNull().ToJsonString();
        tools.ShouldContain("publish");
        tools.ShouldNotContain("archive");
        _publishRan.ShouldBeFalse();
        gate.Requests.ShouldHaveSingleItem().ShouldBe(
            ("youtube", "videos.insert", "Studio.publish", "publish(title=Launch day)"));
        model.Bodies.Count.ShouldBe(2);
        model.Bodies[1]["messages"]!.AsArray()
            .Single(message => message!["role"]?.GetValue<string>() == "tool")!["content"]!
            .GetValue<string>().ShouldContain(QueuedText);
        reply.ShouldBe("It waits for your approval.");
    }

    [Fact]
    public async Task On_The_Azure_AI_Inference_Path_A_Model_Initiated_Call_To_A_Declared_Write_Is_Gated_And_The_Model_Is_Told_It_Is_Queued()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-7");
        var model = new ChatCompletionsScript("Phi-3.5");
        var services = Compose(gate, new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "AzureAIInference",
            ["Scout:Llm:AzureAIInference:ApiKey"] = "test-key",
            ["Scout:Llm:AzureAIInference:Endpoint"] = "https://my-resource.services.ai.azure.com/models",
            ["Scout:Llm:AzureAIInference:Chat:Model"] = "Phi-3.5",
            ["Scout:Llm:AzureAIInference:Embeddings:Model"] = "text-embedding-3-small",
        });
        services.AddSingleton<ILlmProviderFactory>(sp => new Scout.Llm.Providers.AzureAIInference.AzureAIInferenceLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(),
            new HttpClient(model) { BaseAddress = new Uri("https://my-resource.services.ai.azure.com/") }));
        using var provider = services.BuildServiceProvider();

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video", Functions = ["Studio.publish"] }, CancellationToken.None);

        var tools = model.Bodies[0]["tools"].ShouldNotBeNull().ToJsonString();
        tools.ShouldContain("publish");
        tools.ShouldNotContain("archive");
        _publishRan.ShouldBeFalse();
        gate.Requests.ShouldHaveSingleItem().ShouldBe(
            ("youtube", "videos.insert", "Studio.publish", "publish(title=Launch day)"));
        model.Bodies.Count.ShouldBe(2);
        model.Bodies[1]["messages"]!.AsArray()
            .Single(message => message!["role"]?.GetValue<string>() == "tool")!["content"]!
            .GetValue<string>().ShouldContain(QueuedText);
        reply.ShouldBe("It waits for your approval.");
    }

    [Fact]
    public async Task On_The_Hugging_Face_Path_A_Model_Initiated_Call_To_A_Declared_Write_Is_Gated_And_The_Model_Is_Told_It_Is_Queued()
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-7");
        var model = new ChatCompletionsScript("meta-llama/Llama-3.1-8B-Instruct");
        var services = Compose(gate, new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "HuggingFace",
            ["Scout:Llm:HuggingFace:ApiKey"] = "test-key",
            ["Scout:Llm:HuggingFace:Chat:Model"] = "meta-llama/Llama-3.1-8B-Instruct",
            ["Scout:Llm:HuggingFace:Embeddings:Model"] = "sentence-transformers/all-MiniLM-L6-v2",
        });
        services.AddSingleton<ILlmProviderFactory>(sp => new Scout.Llm.Providers.HuggingFace.HuggingFaceLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(), new HttpClient(model)));
        using var provider = services.BuildServiceProvider();

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video", Functions = ["Studio.publish"] }, CancellationToken.None);

        var tools = model.Bodies[0]["tools"].ShouldNotBeNull().ToJsonString();
        tools.ShouldContain("publish");
        tools.ShouldNotContain("archive");
        _publishRan.ShouldBeFalse();
        gate.Requests.ShouldHaveSingleItem().ShouldBe(
            ("youtube", "videos.insert", "Studio.publish", "publish(title=Launch day)"));
        model.Bodies.Count.ShouldBe(2);
        model.Bodies[1]["messages"]!.AsArray()
            .Single(message => message!["role"]?.GetValue<string>() == "tool")!["content"]!
            .GetValue<string>().ShouldContain(QueuedText);
        reply.ShouldBe("It waits for your approval.");
    }

    private ServiceCollection Compose(IWriteGate gate, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddScoutLlm(configuration);
        services.RemoveAll<ILlmProviderFactory>();
        services.AddSingleton(KernelPluginFactory.CreateFromFunctions(
            "Studio",
            [
                KernelFunctionFactory.CreateFromMethod(
                    (string title) =>
                    {
                        _publishRan = true;
                        return $"published {title}";
                    },
                    "publish"),
                KernelFunctionFactory.CreateFromMethod(() => "archived", "archive"),
            ]));
        services.AddWriteFunction("Studio", "publish", "youtube", "videos.insert", summaryArguments: ["title"]);
        services.AddSingleton(gate);
        return services;
    }

    private sealed class GeminiScript : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Bodies.Add(body);
            var chunk = Bodies.Count == 1
                ? """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"""
                    + JsonValue.Create(FirstFunctionName(body)).ToJsonString()
                    + ""","args":{"title":"Launch day"}}}]},"finishReason":"STOP","index":0}]}"""
                : """{"candidates":[{"content":{"role":"model","parts":[{"text":"It waits for your approval."}]},"finishReason":"STOP","index":0}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {chunk}\r\n\r\n", Encoding.UTF8, "text/event-stream"),
            };
        }

        private static string FirstFunctionName(JsonObject body)
            => body["tools"]?[0]?["functionDeclarations"]?[0]?["name"]?.GetValue<string>() ?? "missing-tools";
    }

    private sealed class ChatCompletionsScript(string modelId) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Bodies.Add(body);
            string[] chunks = Bodies.Count == 1
                ?
                [
                    $$$"""{"id":"c1","object":"chat.completion.chunk","created":1,"model":"{{{modelId}}}","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call00001","type":"function","function":{"name":{{{JsonValue.Create(FirstFunctionName(body)).ToJsonString()}}},"arguments":"{\"title\":\"Launch day\"}"}}]},"finish_reason":null}]}""",
                    $$$"""{"id":"c1","object":"chat.completion.chunk","created":1,"model":"{{{modelId}}}","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
                ]
                :
                [
                    $$$"""{"id":"c2","object":"chat.completion.chunk","created":1,"model":"{{{modelId}}}","choices":[{"index":0,"delta":{"role":"assistant","content":"It waits for your approval."},"finish_reason":null}]}""",
                    $$$"""{"id":"c2","object":"chat.completion.chunk","created":1,"model":"{{{modelId}}}","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
                ];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NetworkLikeStream(Encoding.UTF8.GetBytes(
                    string.Concat(chunks.Select(chunk => $"data: {chunk}\n\n")) + "data: [DONE]\n\n")))
                {
                    Headers = { { "Content-Type", "text/event-stream" } },
                },
            };
        }

        private sealed class NetworkLikeStream(byte[] bytes) : MemoryStream(bytes)
        {
            public override bool CanSeek => false;
        }

        private static string FirstFunctionName(JsonObject body)
            => body["tools"]?[0]?["function"]?["name"]?.GetValue<string>() ?? "Studio-publish";
    }
}
