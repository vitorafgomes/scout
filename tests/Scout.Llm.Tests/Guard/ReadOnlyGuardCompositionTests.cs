using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.TextGeneration;
using Scout.Knowledge;
using Scout.Llm.Abstractions;
using Scout.Llm.Configuration;
using Scout.Llm.Guard;
using Scout.Llm.Providers.Ollama;

namespace Scout.Llm.Tests.Guard;

public sealed class ReadOnlyGuardCompositionTests
{
    private const string RefusalText =
        "Scout is read-only: it can explain, summarise and draft, but it will not change code, " +
        "infrastructure, configuration, data or accounts. If you want a change, Scout can describe " +
        "the steps for you to apply.";

    private static IConfiguration GuardedOllama() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Guard:TypeSafe:ApiKey"] = "ts-key",
            })
            .Build();

    private static ServiceProvider Compose(StubTypeSafeHandler typeSafe, bool withAgent)
    {
        var services = new ServiceCollection().AddScoutLlm(GuardedOllama());
        services.AddHttpClient<TypeSafeClient>().ConfigurePrimaryHttpMessageHandler(() => typeSafe);
        if (withAgent)
        {
            services.AddSingleton(sp => ProjectExpertAgentFactory.Create(sp.GetRequiredService<Kernel>()));
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void With_A_Key_The_Kernels_Chat_Service_Is_The_Guard_And_No_Unguarded_Text_Service_Remains()
    {
        using var provider = Compose(StubTypeSafeHandler.Answering(0.9), withAgent: false);
        var kernel = provider.GetRequiredService<Kernel>();

        var kernelChat = kernel.GetRequiredService<IChatCompletionService>();

        kernelChat.ShouldBeOfType<ReadOnlyGuardChatCompletionService>();
        provider.GetRequiredService<IChatCompletionService>().ShouldBeSameAs(kernelChat);
        kernel.Services.GetService<ITextGenerationService>().ShouldBeNull();
    }

    [Fact]
    public void The_Default_TypeSafe_Handler_Recycles_Pooled_Connections_Every_Two_Minutes()
    {
        using var provider = new ServiceCollection().AddScoutLlm(GuardedOllama()).BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(TypeSafeClient));
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        handler.ShouldBeOfType<SocketsHttpHandler>().PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task The_Gateway_Refuses_A_Change_Request_On_The_Direct_Chat_Path()
    {
        using var provider = Compose(StubTypeSafeHandler.Answering(0.9), withAgent: false);

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "restart the prod service" }, CancellationToken.None);

        reply.ShouldBe(RefusalText);
    }

    [Fact]
    public async Task The_Gateway_Refuses_A_Change_Request_On_The_Project_Expert_Agent_Path()
    {
        using var provider = Compose(StubTypeSafeHandler.Answering(0.9), withAgent: true);
        provider.GetService<ChatCompletionAgent>().ShouldNotBeNull();

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "restart the prod service" }, CancellationToken.None);

        reply.ShouldBe(RefusalText);
    }

    [Fact]
    public async Task One_User_Message_With_A_Tool_Round_Trip_Costs_Exactly_One_Jev_Call()
    {
        using var typeSafe = StubTypeSafeHandler.Answering(0.1);
        var modelTurns = 0;
        using var model = new ScriptedModel(() => ++modelTurns == 1 ? Sse(ToolCallChunks) : Sse(AnswerChunks));
        using var modelHttp = new HttpClient(model) { BaseAddress = new Uri("http://localhost:11434/v1/") };
        var probeRuns = 0;
        var services = new ServiceCollection().AddScoutLlm(GuardedOllama());
        services.AddHttpClient<TypeSafeClient>().ConfigurePrimaryHttpMessageHandler(() => typeSafe);
        services.AddSingleton<Scout.Llm.Abstractions.ILlmProviderFactory>(sp =>
            new OllamaLlmProviderFactory(sp.GetRequiredService<IOptions<LlmOptions>>(), modelHttp));
        services.AddSingleton(KernelPluginFactory.CreateFromFunctions(
            "Probe",
            [KernelFunctionFactory.CreateFromMethod(() => ++probeRuns > 0 ? "pong" : "never", "ping")]));
        using var provider = services.BuildServiceProvider();

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "what does ping say?" }, CancellationToken.None);

        reply.ShouldBe("ping says pong");
        probeRuns.ShouldBe(1);
        modelTurns.ShouldBe(2);
        typeSafe.Calls.ShouldBe(1);
    }

    private static readonly string[] ToolCallChunks =
    [
        """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"Probe-ping","arguments":"{}"}}]},"finish_reason":null}]}""",
        """{"id":"c1","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
    ];

    private static readonly string[] AnswerChunks =
    [
        """{"id":"c2","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{"role":"assistant","content":"ping says pong"},"finish_reason":null}]}""",
        """{"id":"c2","object":"chat.completion.chunk","created":1,"model":"llama3.1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
    ];

    private static HttpResponseMessage Sse(IEnumerable<string> chunks) =>
        new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NetworkLikeStream(Encoding.UTF8.GetBytes(
                string.Concat(chunks.Select(chunk => $"data: {chunk}\n\n")) + "data: [DONE]\n\n")))
            {
                Headers = { { "Content-Type", "text/event-stream" } },
            },
        };

    private sealed class NetworkLikeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class ScriptedModel(Func<HttpResponseMessage> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(next());
    }
}
