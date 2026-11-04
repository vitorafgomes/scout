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
        "Queued for the owner's approval on the Review screen of Scout Desktop (action act-7).";

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
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video" }, CancellationToken.None);

        model.Bodies[0]["tools"].ShouldNotBeNull();
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
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video" }, CancellationToken.None);

        _publishRan.ShouldBeFalse();
        gate.Requests.ShouldHaveSingleItem().ShouldBe(
            ("youtube", "videos.insert", "Studio.publish", "publish(title=Launch day)"));
        api.Requests.Count.ShouldBe(2);
        api.Requests[1].Body["input"]!.AsArray()
            .Single(item => item!["type"]?.GetValue<string>() == "function_call_output")!["output"]!
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
}
