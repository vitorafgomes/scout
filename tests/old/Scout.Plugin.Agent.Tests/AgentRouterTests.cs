using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Core.Chat;
using Scout.Core.Hosting;
using Scout.Core.Llm;
using Scout.Core.Plugins;

namespace Scout.Plugin.Agent.Tests;

public class AgentRouterTests
{
    private const string DefaultPrompt = "test-system-prompt";

    [Fact]
    public void Ctor_Throws_On_Null_Services()
    {
        Should.Throw<ArgumentNullException>(() =>
            new AgentRouter(null!, Options.Create(new AgentRouterOptions())));
    }

    [Fact]
    public void Ctor_Throws_On_Null_Options()
    {
        var services = BuildServices(ChatReturning("ok"), withPlugin: true);

        Should.Throw<ArgumentNullException>(() =>
            new AgentRouter(services, null!));
    }

    [Fact]
    public async Task Route_Null_Message_Throws()
    {
        var router = BuildRouter(ChatReturning("ok"), withPlugin: true);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            router.RouteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Route_Returns_Null_When_Kernel_Has_No_Functions()
    {
        // Empty-kernel guard: defer to the streaming fallback.
        var router = BuildRouter(ChatReturning("ok"), withPlugin: false);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "list my repos"),
            CancellationToken.None);

        response.ShouldBeNull();
    }

    [Fact]
    public async Task Route_Returns_Plain_Text_From_Model()
    {
        var router = BuildRouter(ChatReturning("here is your answer"), withPlugin: true);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "list my repos"),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.IsStreaming.ShouldBeFalse();
        response.Text.ShouldBe("here is your answer");
    }

    [Fact]
    public async Task Route_Passes_Auto_FunctionChoiceBehavior_And_Kernel_With_Plugins()
    {
        PromptExecutionSettings? capturedSettings = null;
        Kernel? capturedKernel = null;
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedSettings = ci.ArgAt<PromptExecutionSettings?>(1);
                capturedKernel = ci.ArgAt<Kernel?>(2);
                var msg = new ChatMessageContent(AuthorRole.Assistant, "done");
                return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { msg });
            });

        var router = BuildRouter(chat, withPlugin: true);

        await router.RouteAsync(
            new ChatMessage("u1", "c1", "list my repos"),
            CancellationToken.None);

        capturedSettings.ShouldNotBeNull();
        capturedSettings!.FunctionChoiceBehavior.ShouldNotBeNull();
        capturedSettings.FunctionChoiceBehavior.ShouldBeEquivalentTo(FunctionChoiceBehavior.Auto());

        capturedKernel.ShouldNotBeNull();
        capturedKernel!.Plugins.ShouldContain(p => p.Name == "TestTools");
    }

    [Fact]
    public async Task Route_Sends_Configured_System_Prompt_Then_User_Message()
    {
        ChatHistory? captured = null;
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<ChatHistory>(0);
                var msg = new ChatMessageContent(AuthorRole.Assistant, "done");
                return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { msg });
            });

        var router = BuildRouter(chat, withPlugin: true);

        await router.RouteAsync(
            new ChatMessage("u1", "c1", "what is up"),
            CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Count.ShouldBe(2);
        captured[0].Role.ShouldBe(AuthorRole.System);
        captured[0].Content.ShouldBe(DefaultPrompt);
        captured[1].Role.ShouldBe(AuthorRole.User);
        captured[1].Content.ShouldBe("what is up");
    }

    [Fact]
    public async Task Route_Passes_Temperature_And_MaxTokens()
    {
        PromptExecutionSettings? captured = null;
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<PromptExecutionSettings?>(1);
                var msg = new ChatMessageContent(AuthorRole.Assistant, "done");
                return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { msg });
            });

        var options = new AgentRouterOptions
        {
            SystemPrompt = DefaultPrompt,
            Temperature = 0.55,
            MaxTokens = 256,
        };
        var router = new AgentRouter(
            BuildServices(chat, withPlugin: true),
            Options.Create(options),
            NullLogger<AgentRouter>.Instance);

        await router.RouteAsync(
            new ChatMessage("u1", "c1", "go"),
            CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.ExtensionData.ShouldNotBeNull();
        captured.ExtensionData!["temperature"].ShouldBe(0.55);
        captured.ExtensionData["max_tokens"].ShouldBe(256);
    }

    [Fact]
    public async Task Route_Returns_Safe_Message_When_Model_Returns_Empty()
    {
        var router = BuildRouter(ChatReturning("   "), withPlugin: true);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "list my repos"),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Route_Returns_Safe_Message_When_Chat_Service_Throws()
    {
        // Boundary: a failing model call must not crash the host.
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ChatMessageContent>>>(_ =>
                throw new HttpRequestException("connection refused"));

        var router = BuildRouter(chat, withPlugin: true);

        var response = await router.RouteAsync(
            new ChatMessage("u1", "c1", "list my repos"),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.Text.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Route_Propagates_Cancellation_When_Token_Cancelled()
    {
        var chat = Substitute.For<IChatCompletionService>();
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ChatMessageContent>>>(ci =>
            {
                ci.Arg<CancellationToken>().ThrowIfCancellationRequested();
                throw new InvalidOperationException("should have thrown OCE");
            });

        var router = BuildRouter(chat, withPlugin: true);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            router.RouteAsync(new ChatMessage("u1", "c1", "go"), cts.Token));
    }

    private static AgentRouter BuildRouter(IChatCompletionService chat, bool withPlugin)
    {
        var options = new AgentRouterOptions { SystemPrompt = DefaultPrompt };
        return new AgentRouter(
            BuildServices(chat, withPlugin),
            Options.Create(options),
            NullLogger<AgentRouter>.Instance);
    }

    /// <summary>
    /// Builds a provider exposing a real <see cref="ScoutHost"/> whose
    /// resolved kernel carries the given chat service and (optionally) a test
    /// plugin — mirroring how the router resolves the host at call time.
    /// </summary>
    private static IServiceProvider BuildServices(IChatCompletionService chat, bool withPlugin)
    {
        var registry = new PluginRegistry();
        if (withPlugin)
        {
            registry.Register(new TestToolPlugin());
        }

        var services = new ServiceCollection();
        services.AddSingleton<ScoutHost>(sp => new ScoutHost(
            registry,
            new FakeChatLlmProviderFactory(chat),
            sp,
            NullLogger<ScoutHost>.Instance,
            Options.Create(new ScoutOptions()),
            routers: Array.Empty<IMessageRouter>()));
        return services.BuildServiceProvider();
    }

    private static IChatCompletionService ChatReturning(string content)
    {
        var chat = Substitute.For<IChatCompletionService>();
        var msg = new ChatMessageContent(AuthorRole.Assistant, content);
        chat
            .GetChatMessageContentsAsync(
                Arg.Any<ChatHistory>(),
                Arg.Any<PromptExecutionSettings?>(),
                Arg.Any<Kernel?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { msg }));
        return chat;
    }

    /// <summary>
    /// Registers the given chat service on the kernel builder so the router
    /// resolves it from <see cref="ScoutHost.ResolvedKernel"/>, mirroring how
    /// a real provider factory wires its completion service.
    /// </summary>
    private sealed class FakeChatLlmProviderFactory : ILlmProviderFactory
    {
        private readonly IChatCompletionService _chat;
        public FakeChatLlmProviderFactory(IChatCompletionService chat) => _chat = chat;
        public string Name => "FakeChat";
        public void AddToKernel(IKernelBuilder builder) =>
            builder.Services.AddSingleton(_chat);
    }

    /// <summary>
    /// Minimal read-only plugin contributing one callable function so the
    /// kernel reports invocable functions to the empty-kernel guard.
    /// </summary>
    private sealed class TestToolPlugin : IScoutPlugin
    {
        public string Name => "TestTools";
        public PluginCapabilities Capabilities => PluginCapabilities.ReadOnly;

        public KernelPlugin Build(IServiceProvider services)
        {
            var fn = KernelFunctionFactory.CreateFromMethod(
                () => "[]",
                functionName: "list_repos",
                description: "List repositories for the authenticated user.");
            return KernelPluginFactory.CreateFromFunctions("TestTools", new[] { fn });
        }
    }
}
