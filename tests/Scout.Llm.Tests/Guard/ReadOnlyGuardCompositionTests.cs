using System.Text.Json.Nodes;
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
using Scout.Llm.Providers.None;
using Scout.Llm.Providers.Ollama;
using Scout.Llm.Tests.Providers.Ollama;

namespace Scout.Llm.Tests.Guard;

public sealed class ReadOnlyGuardCompositionTests
{
    private const string RefusalText =
        "Scout can act only through features the owner turned on. No feature with write permission is on; " +
        "the owner enables one on the Features screen of the Scout app.";

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
    public async Task With_Provider_None_And_A_Guard_Key_No_Guard_Is_Registered_And_TypeSafe_Is_Never_Called()
    {
        var typeSafe = StubTypeSafeHandler.Answering(0.9);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "None",
                ["Scout:Guard:TypeSafe:ApiKey"] = "ts-key",
            })
            .Build();
        var services = new ServiceCollection().AddScoutLlm(configuration);
        services.AddHttpClient<TypeSafeClient>().ConfigurePrimaryHttpMessageHandler(() => typeSafe);
        using var provider = services.BuildServiceProvider();

        var kernelChat = provider.GetRequiredService<Kernel>().GetRequiredService<IChatCompletionService>();
        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "restart the prod service" }, CancellationToken.None);

        kernelChat.ShouldBeOfType<NoneChatCompletionService>();
        reply.ShouldBe(NoneLlmProviderFactory.NoProviderMessage);
        typeSafe.Calls.ShouldBe(0);
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
        using var model = new ScriptedModel(() => ++modelTurns == 1 ? ToolCall() : Answer());
        using var modelHttp = new HttpClient(model);
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
            .CompleteAsync(new ChatRequest { UserMessage = "what does ping say?", Functions = ["Probe.ping"] }, CancellationToken.None);

        reply.ShouldBe("ping says pong");
        probeRuns.ShouldBe(1);
        modelTurns.ShouldBe(2);
        typeSafe.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_Grant_Bound_From_Scout_Features_GrantedWrites_Lets_A_Change_Request_Reach_The_Model()
    {
        using var typeSafe = StubTypeSafeHandler.Answering(0.9);
        using var model = new ScriptedModel(Answer);
        using var modelHttp = new HttpClient(model);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Guard:TypeSafe:ApiKey"] = "ts-key",
                ["Scout:Features:GrantedWrites"] = "youtube",
            })
            .Build();
        var services = new ServiceCollection().AddScoutLlm(configuration);
        services.AddHttpClient<TypeSafeClient>().ConfigurePrimaryHttpMessageHandler(() => typeSafe);
        services.AddSingleton<Scout.Llm.Abstractions.ILlmProviderFactory>(sp =>
            new OllamaLlmProviderFactory(sp.GetRequiredService<IOptions<LlmOptions>>(), modelHttp));
        using var provider = services.BuildServiceProvider();

        var reply = await provider.GetRequiredService<IChatGateway>()
            .CompleteAsync(new ChatRequest { UserMessage = "upload the video to my channel" }, CancellationToken.None);

        reply.ShouldBe("ping says pong");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" , ", "")]
    [InlineData("youtube", "youtube")]
    [InlineData(" youtube , github ,", "youtube|github")]
    public void GrantedWrites_Is_A_Comma_Separated_List_Of_Feature_Ids(string value, string expected)
    {
        string.Join('|', new FeatureGrantsOptions { GrantedWrites = value }.GrantedWriteIds()).ShouldBe(expected);
    }

    private static HttpResponseMessage ToolCall()
        => OllamaApiStub.Lines(OllamaApiStub.ToolCallLines("llama3.1", "Probe_ping", new JsonObject()));

    private static HttpResponseMessage Answer()
        => OllamaApiStub.Lines(OllamaApiStub.TextLines("llama3.1", ["ping says pong"]));

    private sealed class ScriptedModel(Func<HttpResponseMessage> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(next());
    }
}
