using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                    new HttpClient(model)));
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

    [Fact]
    public async Task A_Non_Owner_Edit_Is_Recorded_Failed_By_The_Write_Gate()
    {
        var model = new ToolCallingModel(
            YouTubeOperatorPlugin.EditSceneText, """{"project_id":"p1","scene_id":"s2","text":"Shape the loaf gently."}""");
        var gate = new AllowingGate();
        var controlPlane = new RecordingControlPlane();
        using var host = OperatorHost(model, gate, controlPlane);

        await host.Services.GetRequiredService<ConversationConductor>().HandleAsync(
            new ChatMessage("U1", "C1", "change the text of scene s2", DateTimeOffset.UtcNow) { OriginReference = "discord:3333:42" },
            new FinalTextSink(),
            CancellationToken.None);

        gate.Requests.ShouldBe(["youtube | Scout's data folder | youtube_edit_scene_text(project_id=p1, scene_id=s2, text)"]);
        gate.Outcomes.ShouldBe(["failed: InvalidOperationException"]);
        Unescaped(model.Bodies[1]).ShouldContain(YouTubeOperatorPlugin.NotOwner);
        controlPlane.Saves.ShouldBeEmpty();
    }

    private static string Unescaped(string body)
        => JsonNode.Parse(body)!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private sealed class ToolCallingModel(string function = "youtube_make_video", string arguments = """{"topic":"sourdough"}""") : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return OllamaWire.Streamed(Bodies.Count == 1
                ? OllamaWire.ToolCall($"YouTubeOperator_{function}", arguments)
                : OllamaWire.Text("I started it."));
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

        public Task<OperatorAction?> NextApprovedActionAsync(CancellationToken ct) => Task.FromResult<OperatorAction?>(null);

        public Task RecordActionAsync(OperatorAction action, string outcome, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> CheckGrantAsync(OperatorAction action, CancellationToken ct) => Task.FromResult(true);

        public Task<OperatorComments> GetCommentsAsync(CancellationToken ct) => Task.FromResult(new OperatorComments([], [], null, null, null));

        public Task<OperatorComments> SaveCommentsAsync(OperatorComments comments, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetVoiceSamplesDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> EditSceneTextAsync(string projectId, string sceneId, string text, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> RegenerateNarrationAsync(string projectId, string sceneId, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> RegenerateSceneAsync(string projectId, string sceneId, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> SetSceneLockedAsync(string projectId, string sceneId, bool locked, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> ReorderScenesAsync(string projectId, IReadOnlyList<string> sceneIds, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> ProduceProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> DelegateProjectAsync(string projectId, string narrationVoice, string direction, string motion, CancellationToken ct) => throw new NotSupportedException();

        public Task<OperatorProjectState> RequestPresenterAsync(string projectId, string feedback, string prompt, CancellationToken ct) => throw new NotSupportedException();
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
