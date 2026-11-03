using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;
using Scout.Operator;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

public sealed class HostOperatorTurnTests
{
    private static IHost OperatorHost(ToolCallingModel model, AllowingGate gate, RecordingControlPlane controlPlane)
        => new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging();
                services.AddScoutHost(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scout:Llm:Provider"] = "Ollama",
                        ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
                        ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                        ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
                        ["Scout:Plugins:YouTube:ApiKey"] = "youtube-dummy-not-real",
                    })
                    .Build());
                services.RemoveAll<ILlmProviderFactory>();
                services.AddSingleton<ILlmProviderFactory>(sp => new OllamaLlmProviderFactory(
                    sp.GetRequiredService<IOptions<LlmOptions>>(),
                    new HttpClient(model) { BaseAddress = new Uri("http://localhost:11434/v1/") }));
                services.RemoveAll<IWriteGate>();
                services.AddSingleton<IWriteGate>(gate);
                services.RemoveAll<IOperatorControlPlane>();
                services.AddSingleton<IOperatorControlPlane>(controlPlane);
            })
            .Build();

    [Fact]
    public async Task A_Chat_Turn_Whose_Model_Calls_Make_Video_Starts_A_Project_Carrying_The_Turns_Origin_Through_The_Write_Gate()
    {
        var model = new ToolCallingModel();
        var gate = new AllowingGate();
        var controlPlane = new RecordingControlPlane();
        using var host = OperatorHost(model, gate, controlPlane);
        var sink = new FinalTextSink();

        await host.Services.GetRequiredService<ConversationConductor>().HandleAsync(
            new ChatMessage("U1", "C1", "make a video about sourdough", DateTimeOffset.UtcNow) { OriginReference = "discord:3333:42" },
            sink,
            CancellationToken.None);

        gate.Requests.ShouldBe(["youtube | Scout's data folder | youtube_make_video(topic=sourdough)"]);
        var (project, origin) = controlPlane.Saves.ShouldHaveSingleItem();
        (project.Topic, project.Status, origin).ShouldBe(("sourdough", "researching", "discord:3333:42"));
        model.Bodies[1].ShouldContain("Started a video project on sourdough; I will post here when the script is ready.");
        sink.FinalText.ShouldBe("I started it.");
        gate.Outcomes.ShouldBe(["succeeded"]);
    }

    [Fact]
    public async Task A_Save_The_Control_Plane_Refuses_Is_Recorded_As_Failed_Never_Succeeded_And_The_Model_Hears_Why()
    {
        const string refusal = "The owner has not allowed youtube to write; allow it on the Features screen of Scout Desktop.";
        var model = new ToolCallingModel();
        var gate = new AllowingGate();
        var controlPlane = new RecordingControlPlane { SaveFailure = refusal };
        using var host = OperatorHost(model, gate, controlPlane);

        await host.Services.GetRequiredService<ConversationConductor>().HandleAsync(
            new ChatMessage("U1", "C1", "make a video about sourdough", DateTimeOffset.UtcNow) { OriginReference = "discord:3333:42" },
            new FinalTextSink(),
            CancellationToken.None);

        gate.Outcomes.ShouldHaveSingleItem().ShouldStartWith("failed: ");
        gate.Outcomes.ShouldNotContain("succeeded");
        model.Bodies[1].ShouldContain($"Could not start a video project on sourdough: {refusal}");
        host.Services.GetRequiredService<System.Threading.Channels.Channel<QueuedProject>>().Reader.TryRead(out _).ShouldBeFalse();
    }

    private sealed class ToolCallingModel : HttpMessageHandler
    {
        private static readonly string[] ToolCall =
        [
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"YouTubeOperator-youtube_make_video","arguments":"{\"topic\":\"sourdough\"}"}}]},"finish_reason":null}]}""",
            """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        ];

        private static readonly string[] Answer =
        [
            """{"id":"c2","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{"role":"assistant","content":"I started it."},"finish_reason":null}]}""",
            """{"id":"c2","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        ];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var chunks = Bodies.Count == 1 ? ToolCall : Answer;
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

    private sealed class AllowingGate : IWriteGate
    {
        public List<string> Requests { get; } = [];

        public List<string> Outcomes { get; } = [];

        public Task<WriteGateDecision> RequestAsync(
            string featureId, string scope, string target, string summary, CancellationToken cancellationToken)
        {
            Requests.Add($"{featureId} | {scope} | {summary}");
            return Task.FromResult(new WriteGateDecision(WriteGateDecisionKind.Allowed, string.Empty, null));
        }

        public Task RecordAsync(
            string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingControlPlane : IOperatorControlPlane
    {
        public List<(OperatorProjectState Project, string? Origin)> Saves { get; } = [];

        public string? SaveFailure { get; init; }

        public Task<OperatorProjectState> SaveProjectAsync(OperatorProjectState project, string? originReference, CancellationToken ct)
        {
            Saves.Add((project, originReference));
            return SaveFailure is null
                ? Task.FromResult(project with { Id = "p1" })
                : Task.FromException<OperatorProjectState>(new InvalidOperationException(SaveFailure));
        }

        public Task<OperatorMandate> GetMandateAsync(CancellationToken ct) =>
            Task.FromResult(new OperatorMandate("Teach baking", string.Empty, [], string.Empty, "English", string.Empty, ["diets"], string.Empty, string.Empty));

        public Task<QueuedProject?> NextQueuedAsync(CancellationToken ct) => Task.FromResult<QueuedProject?>(null);

        public Task<OperatorProjectState> GetProjectAsync(string id, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<OperatorProjectState>> ListRecentAsync(int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetMediaDirectoryAsync(string projectId, CancellationToken ct) => throw new NotSupportedException();

        public Task ReportReadinessAsync(OperatorReadiness readiness, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FinalTextSink : IResponseSink
    {
        public string? FinalText { get; private set; }

        public RenderCapabilities Capabilities { get; } = new() { FineGrainedEdit = true, MaxBodyLength = 4096 };

        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(string finalText, IReadOnlyList<Citation> citations, CancellationToken cancellationToken)
        {
            FinalText = finalText;
            return Task.CompletedTask;
        }

        public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken)
        {
            FinalText = reason;
            return Task.CompletedTask;
        }
    }
}
