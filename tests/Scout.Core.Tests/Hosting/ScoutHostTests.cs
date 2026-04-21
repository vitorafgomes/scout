using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scout.Core.Chat;
using Scout.Core.Hosting;
using Scout.Core.Plugins;
using Scout.Core.Tests.Fakes;

namespace Scout.Core.Tests.Hosting;

public class ScoutHostTests
{
    [Fact]
    public async Task HandleAsync_Null_Message_Throws()
    {
        var host = BuildHost(registry: new PluginRegistry());

        Func<Task> act = () => host.HandleAsync(null!, CancellationToken.None);

        await act.ShouldThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task HandleAsync_Returns_Handshake_When_No_Router_Matches()
    {
        var host = BuildHost(registry: new PluginRegistry());

        var response = await host.HandleAsync(
            new ChatMessage("u1", "c1", "hello"),
            CancellationToken.None);

        response.Text.ShouldContain("Scout is online");
    }

    [Fact]
    public async Task HandleAsync_Reports_Loaded_Plugin_Count_In_Handshake()
    {
        var registry = new PluginRegistry();
        registry.Register(new FakeReadOnlyPlugin(name: "first"));
        registry.Register(new FakeReadOnlyPlugin(name: "second"));

        var host = BuildHost(registry);

        var response = await host.HandleAsync(
            new ChatMessage("u1", "c1", "hi there"),
            CancellationToken.None);

        response.Text.ShouldContain("2 plugin");
    }

    [Fact]
    public async Task HandleAsync_Returns_First_Matching_Router_Response()
    {
        var firstRouter = new StaticRouter(ChatResponse.Plain("from first"));
        var secondRouter = new StaticRouter(ChatResponse.Plain("from second"));

        var host = BuildHost(registry: new PluginRegistry(),
            routers: [firstRouter, secondRouter]);

        var response = await host.HandleAsync(
            new ChatMessage("u1", "c1", "whatever"),
            CancellationToken.None);

        response.Text.ShouldBe("from first");
    }

    [Fact]
    public async Task HandleAsync_Skips_Null_Returning_Router()
    {
        var first = new StaticRouter(response: null);
        var second = new StaticRouter(ChatResponse.Plain("hit second"));

        var host = BuildHost(registry: new PluginRegistry(),
            routers: [first, second]);

        var response = await host.HandleAsync(
            new ChatMessage("u1", "c1", "whatever"),
            CancellationToken.None);

        response.Text.ShouldBe("hit second");
    }

    [Fact]
    public async Task HandleAsync_Router_Exception_Produces_Safe_Error_Response()
    {
        var throwing = new ThrowingRouter();

        var host = BuildHost(registry: new PluginRegistry(), routers: [throwing]);

        var response = await host.HandleAsync(
            new ChatMessage("u1", "c1", "boom"),
            CancellationToken.None);

        response.Text.ShouldContain("something went wrong");
    }

    [Fact]
    public async Task HandleAsync_Propagates_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var router = new CooperativeRouter();
        var host = BuildHost(registry: new PluginRegistry(), routers: [router]);

        Func<Task> act = () => host.HandleAsync(
            new ChatMessage("u1", "c1", "hi"),
            cts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Ctor_Throws_When_Registry_Null()
    {
        Action create = () => new ScoutHost(
            registry: null!,
            llmFactory: new FakeLlmProviderFactory(),
            services: new ServiceCollection().BuildServiceProvider(),
            logger: NullLogger<ScoutHost>.Instance,
            options: Options.Create(new ScoutOptions()),
            routers: []);

        create.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Throws_When_LlmFactory_Null()
    {
        Action create = () => new ScoutHost(
            registry: new PluginRegistry(),
            llmFactory: null!,
            services: new ServiceCollection().BuildServiceProvider(),
            logger: NullLogger<ScoutHost>.Instance,
            options: Options.Create(new ScoutOptions()),
            routers: []);

        create.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Throws_When_Routers_Null()
    {
        Action create = () => new ScoutHost(
            registry: new PluginRegistry(),
            llmFactory: new FakeLlmProviderFactory(),
            services: new ServiceCollection().BuildServiceProvider(),
            logger: NullLogger<ScoutHost>.Instance,
            options: Options.Create(new ScoutOptions()),
            routers: null!);

        create.ShouldThrow<ArgumentNullException>();
    }

    private static ScoutHost BuildHost(
        PluginRegistry registry,
        IEnumerable<IMessageRouter>? routers = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new ScoutHost(
            registry,
            new FakeLlmProviderFactory(),
            services,
            NullLogger<ScoutHost>.Instance,
            Options.Create(new ScoutOptions()),
            routers ?? Array.Empty<IMessageRouter>());
    }

    private sealed class StaticRouter : IMessageRouter
    {
        private readonly ChatResponse? _response;
        public StaticRouter(ChatResponse? response) => _response = response;
        public Task<ChatResponse?> RouteAsync(ChatMessage message, CancellationToken ct) =>
            Task.FromResult(_response);
    }

    private sealed class ThrowingRouter : IMessageRouter
    {
        public Task<ChatResponse?> RouteAsync(ChatMessage message, CancellationToken ct) =>
            throw new InvalidOperationException("router boom");
    }

    private sealed class CooperativeRouter : IMessageRouter
    {
        public Task<ChatResponse?> RouteAsync(ChatMessage message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ChatResponse?>(ChatResponse.Plain("unreachable"));
        }
    }
}
