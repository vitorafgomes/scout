using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Scout.Knowledge;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;

namespace Scout.Llm.Tests.Guard;

public sealed class WriteGateCompositionTests
{
    private const string QueuedText =
        "Queued for the owner's approval on the Review screen of Scout Desktop (action act-7).";

    private bool _publishRan;

    private ServiceProvider Compose(IWriteGate gate, ScriptedModel model, bool withAgent)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Llm:Provider"] = "Ollama" })
            .Build();
        var services = new ServiceCollection().AddScoutLlm(configuration);
        services.AddSingleton<ILlmProviderFactory>(sp => new OllamaLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(),
            new HttpClient(model) { BaseAddress = new Uri("http://localhost:11434/v1/") }));
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
        if (withAgent)
        {
            services.AddSingleton(sp => ProjectExpertAgentFactory.Create(sp.GetRequiredService<Kernel>()));
        }

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Model_Initiated_Call_To_A_Declared_Write_Is_Gated_And_The_Model_Is_Told_It_Is_Queued(bool agentPath)
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-7");
        using var model = new ScriptedModel();
        using var provider = Compose(gate, model, agentPath);
        (provider.GetService<ChatCompletionAgent>() is not null).ShouldBe(agentPath);

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video" }, CancellationToken.None);

        _publishRan.ShouldBeFalse();
        gate.Requests.ShouldHaveSingleItem().ShouldBe(
            ("youtube", "videos.insert", "Studio.publish", "publish(title=Launch day)"));
        model.Bodies.Count.ShouldBe(2);
        model.Bodies[1].ShouldContain(QueuedText);
        reply.ShouldBe("It waits for your approval.");
    }

    [Fact]
    public void A_Declared_Write_Without_A_Registered_Gate_Fails_The_Kernel_Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Llm:Provider"] = "Ollama" })
            .Build();
        using var provider = new ServiceCollection()
            .AddScoutLlm(configuration)
            .AddWriteFunction("Studio", "publish", "youtube", "videos.insert")
            .BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<Kernel>())
            .Message.ShouldContain(nameof(IWriteGate));
    }

    internal sealed class ScriptedModel : HttpMessageHandler
    {
        private static readonly string[] ToolCallChunks =
        [
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"Studio-publish","arguments":"{\"title\":\"Launch day\"}"}}]},"finish_reason":null}]}""",
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        ];

        private static readonly string[] AnswerChunks =
        [
            """{"id":"c2","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{"role":"assistant","content":"It waits for your approval."},"finish_reason":null}]}""",
            """{"id":"c2","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        ];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var chunks = Bodies.Count == 1 ? ToolCallChunks : AnswerChunks;
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
    }
}
