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

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task HandleAsync_Returns_NonEmpty_ChatResponse()
    {
        var host = BuildHost(registry: new PluginRegistry());

        var response = await host.HandleAsync(
            new ChatMessage("u1", "c1", "hello"),
            CancellationToken.None);

        response.Text.Should().NotBeNullOrWhiteSpace();
        response.Citations.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_Reports_Loaded_Plugin_Count()
    {
        var registry = new PluginRegistry();
        registry.Register(new FakeReadOnlyPlugin(name: "first"));
        registry.Register(new FakeReadOnlyPlugin(name: "second"));

        var host = BuildHost(registry);

        var response = await host.HandleAsync(
            new ChatMessage("u1", "c1", "status"),
            CancellationToken.None);

        response.Text.Should().Contain("2 plugin");
    }

    [Fact]
    public void Ctor_Throws_When_Registry_Null()
    {
        Action create = () => new ScoutHost(
            registry: null!,
            llmFactory: new FakeLlmProviderFactory(),
            services: new ServiceCollection().BuildServiceProvider(),
            logger: NullLogger<ScoutHost>.Instance,
            options: Options.Create(new ScoutOptions()));

        create.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Throws_When_LlmFactory_Null()
    {
        Action create = () => new ScoutHost(
            registry: new PluginRegistry(),
            llmFactory: null!,
            services: new ServiceCollection().BuildServiceProvider(),
            logger: NullLogger<ScoutHost>.Instance,
            options: Options.Create(new ScoutOptions()));

        create.Should().Throw<ArgumentNullException>();
    }

    private static ScoutHost BuildHost(PluginRegistry registry)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new ScoutHost(
            registry,
            new FakeLlmProviderFactory(),
            services,
            NullLogger<ScoutHost>.Instance,
            Options.Create(new ScoutOptions()));
    }
}
