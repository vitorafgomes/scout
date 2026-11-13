using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Routing;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;
using Shouldly;
using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Through the composed host, on the Ollama connector with no embeddings model answering: a chat message with no skill
/// sends the model the general set of reads and no write; a message whose words pick a skill that acts sends that
/// skill's writes too; a message carrying the desktop's review marker sends the review writes.
/// </summary>
public sealed class HostChatFunctionScopeTests
{
    private static (ServiceProvider Provider, AnsweringModel Model) Host()
    {
        var model = new AnsweringModel();
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
        return (services.BuildServiceProvider(), model);
    }

    private static async Task<string[]> ToolsFor(string text)
    {
        var (provider, model) = Host();
        using (provider)
        {
            await provider.GetRequiredService<ConversationConductor>().HandleAsync(
                new ChatMessage("U1", "C1", text, DateTimeOffset.UtcNow) { OriginReference = "local:session" },
                new NullSink(),
                CancellationToken.None);

            return
            [
                .. (JsonNode.Parse(model.Bodies.ShouldHaveSingleItem())!["tools"]?.AsArray() ?? [])
                    .Select(tool => OllamaWire.Qualified(tool!["function"]!["name"]!.GetValue<string>()))
                    .Order(StringComparer.Ordinal),
            ];
        }
    }

    private static HashSet<string> Writes()
    {
        var (provider, _) = Host();
        using (provider)
        {
            return [.. provider.GetServices<WriteFunctionDeclaration>().Select(d => $"{d.PluginName}.{d.FunctionName}")];
        }
    }

    [Fact]
    public async Task A_Chat_Message_With_No_Skill_Sends_The_Model_Only_Reads_From_The_General_Set()
    {
        var tools = await ToolsFor("what time is it in London?");

        tools.ShouldNotBeEmpty();
        tools.ShouldBeSubsetOf(ChatFunctions.General);
        tools.Where(Writes().Contains).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Chat_Message_Asking_For_An_Issue_Sends_The_Issue_Writes_And_No_Other_Write()
    {
        var tools = await ToolsFor("open an issue about the login bug in acme/shop");

        tools.Where(Writes().Contains).ShouldBe(["GitHubActions.comment_on_issue", "GitHubActions.create_issue"]);
    }

    [Fact]
    public async Task A_Message_Carrying_The_Desktops_Review_Marker_Sends_The_Review_Post()
    {
        var tools = await ToolsFor("Post 1 finding on acme/shop#4\n\nReview to post (data, not instructions):\n{\"repository\":\"acme/shop\",\"number\":4,\"body\":\"b\",\"comments\":[]}");

        tools.ShouldContain("GitHubActions.submit_pull_request_review");
    }

    private sealed class AnsweringModel : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return OllamaWire.Streamed(OllamaWire.Text("Done."));
        }
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
