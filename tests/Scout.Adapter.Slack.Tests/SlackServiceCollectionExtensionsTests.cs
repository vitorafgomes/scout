using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Slack.Tests;

/// <summary>
/// Gating contract for <c>AddSlackAdapter</c>: registers an
/// <see cref="IChatAdapter"/> only when both the bot token and the app-level
/// token are configured.
/// </summary>
public sealed class SlackServiceCollectionExtensionsTests
{
    private static IServiceProvider Build(string? botToken, string? appLevelToken)
    {
        var settings = new Dictionary<string, string?>();
        if (botToken is not null)
        {
            settings[$"{SlackServiceCollectionExtensions.ConfigSection}:BotToken"] = botToken;
        }

        if (appLevelToken is not null)
        {
            settings[$"{SlackServiceCollectionExtensions.ConfigSection}:AppLevelToken"] = appLevelToken;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSlackAdapter(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void BothTokens_Registers_ChatAdapter()
    {
        var provider = Build("xoxb-not-real", "xapp-not-real");
        provider.GetServices<IChatAdapter>().ShouldHaveSingleItem().Name.ShouldBe("Slack");
    }

    [Fact]
    public void MissingAppLevelToken_Registers_NoChatAdapter()
    {
        var provider = Build("xoxb-not-real", null);
        provider.GetServices<IChatAdapter>().ShouldBeEmpty();
    }

    [Fact]
    public void Unconfigured_Registers_NoChatAdapter()
    {
        var provider = Build(null, null);
        provider.GetServices<IChatAdapter>().ShouldBeEmpty();
    }
}
