using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Through the composed host, on the Ollama connector with no embeddings model answering: a code review of a named pull
/// request is offered the review's writes on its pull request and no function that opens an issue or starts an audit,
/// and loading the audit skill's text in the middle of the turn offers it nothing more.
/// </summary>
public sealed class HostReviewTurnTests
{
    private static ServiceProvider Host(ScriptedModel model, RecordingGate gate)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddScoutHost(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scout:Llm:Provider"] = "Ollama",
            ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
            ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
            ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
            ["Scout:Plugins:GitHub:Token"] = "github-dummy-not-real",
            ["Scout:WorkerGate:SocketPath"] = "/run/scout/cp.sock",
            ["Scout:WorkerGate:Token"] = "t0ken",
        }).Build());
        services.RemoveAll<ILlmProviderFactory>();
        services.AddSingleton<ILlmProviderFactory>(sp => new OllamaLlmProviderFactory(
            sp.GetRequiredService<IOptions<LlmOptions>>(),
            new HttpClient(model)));
        services.RemoveAll<IWriteGate>();
        services.AddSingleton<IWriteGate>(gate);
        return services.BuildServiceProvider();
    }

    private static string[] Tools(string body) =>
    [
        .. (JsonNode.Parse(body)!["tools"]?.AsArray() ?? [])
            .Select(tool => OllamaWire.Qualified(tool!["function"]!["name"]!.GetValue<string>()))
            .Order(StringComparer.Ordinal),
    ];

    [Fact]
    public async Task A_Review_Turn_That_Loads_The_Audit_Skill_And_Calls_Create_Issue_Is_Offered_Only_The_Reviews_Pull_Request_Writes_And_Opens_No_Issue()
    {
        var model = new ScriptedModel(
            ("Skills_load_skill", """{"name":"code-audit"}"""),
            ("GitHubActions_create_issue", """{"owner":"acme","repo":"shop","title":"Bug","body":"Found in the review."}"""));
        var gate = new RecordingGate();
        using var provider = Host(model, gate);

        await provider.GetRequiredService<ConversationConductor>().HandleAsync(
            new ChatMessage("U1", "C1", "review PR 5 in acme/shop", DateTimeOffset.UtcNow) { OriginReference = "local:session" },
            new NullSink(),
            CancellationToken.None);

        model.Bodies.Count.ShouldBe(3);
        model.Bodies[1].ShouldContain("# Code audit", Case.Sensitive, "load_skill ran and answered the audit's text");
        var writes = provider.GetServices<WriteFunctionDeclaration>().Select(d => $"{d.PluginName}.{d.FunctionName}").ToHashSet();
        foreach (var body in model.Bodies)
        {
            Tools(body).Where(writes.Contains).ShouldBe(
            [
                "GitHubActions.comment_on_pull_request",
                "GitHubActions.give_pull_request_verdict",
                "GitHubActions.reply_to_review_comment",
                "GitHubActions.run_pull_request_tests",
                "GitHubActions.snapshot_repository",
                "GitHubActions.submit_pull_request_review",
            ], "a code review must always add comments on the pull request and never open issues");
        }

        gate.Requests.ShouldBeEmpty("the issue the model asked for never reached the write gate, so nothing was queued");
    }

    private static async Task<string[]> FollowUpTools(string followUp)
    {
        var model = new ScriptedModel();
        using var provider = Host(model, new RecordingGate());
        var conductor = provider.GetRequiredService<ConversationConductor>();
        foreach (var text in new[] { "review PR 5 in acme/shop", followUp })
        {
            await conductor.HandleAsync(
                new ChatMessage("U1", "C1", text, DateTimeOffset.UtcNow) { OriginReference = "local:session" },
                new NullSink(),
                CancellationToken.None);
        }

        model.Bodies.Count.ShouldBe(2);
        return Tools(model.Bodies[1]);
    }

    [Theory]
    [InlineData("para a auditoria do acme/shop")]
    [InlineData("stop the audit of acme/shop")]
    [InlineData("pause the audit")]
    [InlineData("continua a auditoria")]
    [InlineData("start an audit of acme/shop")]
    [InlineData("audita o acme/shop")]
    [InlineData("como está a auditoria")]
    public async Task In_A_Review_Conversation_A_Message_That_Names_The_Audit_Is_Offered_The_Audits_Controls(string followUp)
    {
        var tools = await FollowUpTools(followUp);

        tools.ShouldContain("GitHubActions.stop_repository_audit", "the owner keeps control of a running audit in a review conversation");
        tools.ShouldContain("GitHubActions.pause_repository_audit");
        tools.ShouldContain("GitHubActions.start_repository_audit");
        tools.ShouldNotContain("GitHubActions.submit_pull_request_review");
    }

    [Theory]
    [InlineData("look for security bugs too")]
    [InlineData("e performance?")]
    [InlineData("procura bugs também")]
    public async Task In_A_Review_Conversation_A_Follow_Up_That_Does_Not_Name_The_Audit_Stays_The_Review(string followUp)
    {
        var tools = await FollowUpTools(followUp);

        tools.ShouldContain("GitHubActions.submit_pull_request_review");
        tools.ShouldNotContain("GitHubActions.create_issue", "a code review must always add comments on the pull request and never open issues");
        tools.ShouldNotContain("GitHubActions.start_repository_audit");
    }

    private sealed class ScriptedModel(params (string Name, string Arguments)[] calls) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var round = Bodies.Count;
            return OllamaWire.Streamed(round <= calls.Length
                ? OllamaWire.ToolCall(calls[round - 1].Name, calls[round - 1].Arguments)
                : OllamaWire.Text("Done."));
        }
    }

    private sealed class RecordingGate : IWriteGate
    {
        public List<string> Requests { get; } = [];

        public Task<WriteGateDecision> RequestAsync(string featureId, string scope, string target, string summary, CancellationToken cancellationToken)
        {
            Requests.Add(target);
            return Task.FromResult(new WriteGateDecision(WriteGateDecisionKind.Allowed, string.Empty, null));
        }

        public Task RecordAsync(string featureId, string scope, string target, string outcome, string? actionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NullSink : IResponseSink
    {
        public RenderCapabilities Capabilities { get; } = new() { FineGrainedEdit = true, MaxBodyLength = 4096 };

        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(string cumulativeText, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(string finalText, IReadOnlyList<Citation> citations, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FailAsync(string finalTextSoFar, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
