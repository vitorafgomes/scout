using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Scout.Knowledge;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;
using Scout.Llm.Tests.Providers.Ollama;

namespace Scout.Llm.Tests.Guard;

public sealed class WriteGateCompositionTests
{
    private const string QueuedText =
        "Queued for the owner's approval: an Approve card is waiting in the chat and in Approvals of Scout Desktop (action act-7).";

    private bool _publishRan;

    private ServiceProvider Compose(IWriteGate gate, ScriptedModel model, bool withAgent, bool carriesPayload = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scout:Llm:Provider"] = "Ollama" })
            .Build();
        var services = new ServiceCollection().AddScoutLlm(configuration);
        services.AddSingleton<ILlmProviderFactory>(sp => new OllamaLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(),
            new HttpClient(model)));
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
        services.AddWriteFunction("Studio", "publish", "youtube", "videos.insert", summaryArguments: ["title"], carriesPayload: carriesPayload);
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
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video", Functions = ["Studio.publish"] }, CancellationToken.None);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Argument_The_Model_Adds_That_The_Function_Does_Not_Declare_Never_Reaches_The_Payload_Queued_For_The_Owner(bool agentPath)
    {
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Queued, "irreversible", "act-7");
        using var model = new ScriptedModel("""{"title":"Launch day","testsLine":"A comment the owner never saw."}""");
        using var provider = Compose(gate, model, agentPath, carriesPayload: true);

        await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "publish the launch video", Functions = ["Studio.publish"] }, CancellationToken.None);

        gate.Payloads.ShouldHaveSingleItem().ShouldBe("""{"title":"Launch day"}""");
        _publishRan.ShouldBeFalse();
    }

    internal sealed class ScriptedModel(string arguments = """{"title":"Launch day"}""") : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return OllamaApiStub.Lines(Bodies.Count == 1
                ? OllamaApiStub.ToolCallLines("llama3.1", "Studio_publish", JsonNode.Parse(arguments)!.AsObject())
                : OllamaApiStub.TextLines("llama3.1", ["It waits for your approval."]));
        }
    }
}
