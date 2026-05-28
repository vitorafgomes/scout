using Microsoft.Extensions.DependencyInjection;
using Scout.Core.Chat;
using Scout.Core.Plugins;

namespace Scout.Plugin.Help.Tests;

public class HelpServiceCollectionExtensionsTests
{
    [Fact]
    public void AddHelpPlugin_registers_HelpPlugin_as_IScoutPlugin()
    {
        var services = new ServiceCollection();

        services.AddHelpPlugin();

        using var provider = services.BuildServiceProvider();
        var plugin = provider.GetRequiredService<IScoutPlugin>();

        plugin.ShouldBeOfType<HelpPlugin>();
        plugin.Name.ShouldBe(HelpPlugin.PluginName);
        plugin.Capabilities.IsReadOnly.ShouldBeTrue();
    }

    [Fact]
    public void AddHelpPlugin_registers_HelpMessageRouter_among_IMessageRouters()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMessageRouter, FakeRouter>();

        services.AddHelpPlugin();

        using var provider = services.BuildServiceProvider();
        var routers = provider.GetServices<IMessageRouter>().ToArray();

        routers.Length.ShouldBe(2);
        routers.ShouldContain(r => r is HelpMessageRouter);
        routers.ShouldContain(r => r is FakeRouter);
    }

    [Fact]
    public void AddHelpPlugin_throws_on_null_services()
    {
        Action act = () => HelpServiceCollectionExtensions.AddHelpPlugin(null!);

        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void AddHelpPlugin_registrations_are_singleton()
    {
        var services = new ServiceCollection();

        services.AddHelpPlugin();

        using var provider = services.BuildServiceProvider();
        var plugin1 = provider.GetRequiredService<IScoutPlugin>();
        var plugin2 = provider.GetRequiredService<IScoutPlugin>();
        var router1 = provider.GetRequiredService<IMessageRouter>();
        var router2 = provider.GetRequiredService<IMessageRouter>();

        plugin1.ShouldBeSameAs(plugin2);
        router1.ShouldBeSameAs(router2);
    }

    private sealed class FakeRouter : IMessageRouter
    {
        public Task<ChatResponse?> RouteAsync(
            ChatMessage message,
            CancellationToken cancellationToken) =>
            Task.FromResult<ChatResponse?>(null);
    }
}
