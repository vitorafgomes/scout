using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Scout.Knowledge;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;
using Scout.Llm.Tests.Guard;
using Scout.Llm.Tests.Providers.Ollama;

namespace Scout.Llm.Tests;

/// <summary>
/// A turn is offered only the functions its request declares, on the direct chat path and on the Project Expert
/// agent path: the tools in the request the model receives are exactly that set, an empty or missing set sends no
/// tools, and a call to a function the turn did not declare never runs and is logged without its arguments.
/// </summary>
public sealed class ChatGatewayFunctionScopeTests
{
    private bool _publishRan;
    private bool _statusRan;

    private ServiceProvider Compose(ScopeModel model, bool withAgent, FakeWriteGate gate, ILogger<ChatGateway>? logger = null)
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
                KernelFunctionFactory.CreateFromMethod(
                    () =>
                    {
                        _statusRan = true;
                        return "two videos";
                    },
                    "status"),
            ]));
        services.AddSingleton(KernelPluginFactory.CreateFromFunctions(
            "Other", [KernelFunctionFactory.CreateFromMethod(() => "thing", "thing")]));
        services.AddWriteFunction("Studio", "publish", "youtube", "videos.insert", summaryArguments: ["title"]);
        services.AddSingleton<IWriteGate>(gate);
        if (logger is not null)
        {
            services.AddSingleton(logger);
        }

        if (withAgent)
        {
            services.AddSingleton(sp => ProjectExpertAgentFactory.Create(sp.GetRequiredService<Kernel>()));
        }

        return services.BuildServiceProvider();
    }

    private static string[] ToolNames(string body) =>
        JsonNode.Parse(body)!["tools"]!.AsArray()
            .Select(tool => tool!["function"]!["name"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_Tools_The_Model_Receives_Are_Exactly_The_Functions_The_Turn_Declares(bool agentPath)
    {
        using var model = new ScopeModel(toolCall: null);
        using var provider = Compose(model, agentPath, FakeWriteGate.Answering(WriteGateDecisionKind.Allowed));
        (provider.GetService<ChatCompletionAgent>() is not null).ShouldBe(agentPath);

        await provider.GetRequiredService<IChatGateway>().CompleteAsync(
            new ChatRequest { UserMessage = "how many videos?", Functions = ["Studio.status", "Other.thing", "Missing.nothing"] },
            CancellationToken.None);

        ToolNames(model.Bodies.ShouldHaveSingleItem()).ShouldBe(["Other_thing", "Studio_status"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_Empty_Or_Missing_Set_Sends_The_Model_No_Tools(bool agentPath, bool missing)
    {
        using var model = new ScopeModel(toolCall: null);
        using var provider = Compose(model, agentPath, FakeWriteGate.Answering(WriteGateDecisionKind.Allowed));
        var request = missing
            ? new ChatRequest { UserMessage = "hello" }
            : new ChatRequest { UserMessage = "hello", Functions = [] };

        var reply = await provider.GetRequiredService<IChatGateway>().CompleteAsync(request, CancellationToken.None);

        JsonNode.Parse(model.Bodies.ShouldHaveSingleItem())!["tools"].ShouldBeNull();
        reply.ShouldBe("Done.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Call_To_A_Function_The_Turn_Did_Not_Declare_Never_Runs_And_Is_Logged_Naming_The_Function_And_The_Skill(bool agentPath)
    {
        using var model = new ScopeModel(toolCall: "Studio_publish");
        var gate = FakeWriteGate.Answering(WriteGateDecisionKind.Allowed);
        var logger = new RecordingLogger();
        using var provider = Compose(model, agentPath, gate, logger);

        var reply = await provider.GetRequiredService<IChatGateway>().CompleteAsync(
            new ChatRequest { UserMessage = "publish the launch video", Functions = ["Studio.status"], Skills = ["youtube-operator"] },
            CancellationToken.None);

        _publishRan.ShouldBeFalse();
        _statusRan.ShouldBeFalse();
        gate.Requests.ShouldBeEmpty();
        model.Bodies.Count.ShouldBe(2);
        var (_, warning) = logger.Entries.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.ShouldBe(
            "The model called Studio_publish, which this turn (youtube-operator) does not declare; it was not offered and did not run.");
        warning.ShouldNotContain("Launch day");
        reply.ShouldBe("Done.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Declared_Function_The_Model_Calls_Runs_And_Logs_No_Warning(bool agentPath)
    {
        using var model = new ScopeModel(toolCall: "Studio_status");
        var logger = new RecordingLogger();
        using var provider = Compose(model, agentPath, FakeWriteGate.Answering(WriteGateDecisionKind.Allowed), logger);

        await provider.GetRequiredService<IChatGateway>().CompleteAsync(
            new ChatRequest { UserMessage = "how many videos?", Functions = ["Studio.status"] },
            CancellationToken.None);

        _statusRan.ShouldBeTrue();
        model.Bodies[1].ShouldContain("two videos");
        logger.Entries.Where(e => e.Level == LogLevel.Warning).ShouldBeEmpty();
    }

    internal sealed class ScopeModel(string? toolCall) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return OllamaApiStub.Lines(toolCall is not null && Bodies.Count == 1
                ? OllamaApiStub.ToolCallLines("llama3.1", toolCall, new JsonObject { ["title"] = "Launch day" })
                : OllamaApiStub.TextLines("llama3.1", ["Done."]));
        }
    }

    private sealed class RecordingLogger : ILogger<ChatGateway>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
