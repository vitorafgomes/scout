using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Discord.Tests;

/// <summary>
/// Gating contract for <c>AddDiscordAdapter</c>: registers an
/// <see cref="IChatAdapter"/> only when a bot token is configured.
/// </summary>
public sealed class DiscordServiceCollectionExtensionsTests
{
    private static IServiceProvider Build(string? botToken)
    {
        var settings = new Dictionary<string, string?>();
        if (botToken is not null)
        {
            settings[$"{DiscordServiceCollectionExtensions.ConfigSection}:BotToken"] = botToken;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDiscordAdapter(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Configured_Registers_ChatAdapter()
    {
        var provider = Build("test-token-not-real");
        provider.GetServices<IChatAdapter>().ShouldHaveSingleItem().Name.ShouldBe("Discord");
    }

    [Fact]
    public void Unconfigured_Registers_NoChatAdapter()
    {
        var provider = Build(null);
        provider.GetServices<IChatAdapter>().ShouldBeEmpty();
    }
}
