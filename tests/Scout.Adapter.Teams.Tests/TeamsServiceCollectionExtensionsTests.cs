using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;

namespace Scout.Adapter.Teams.Tests;

/// <summary>
/// Gating contract for <c>AddTeamsAdapter</c>: registers an
/// <see cref="IChatAdapter"/> only when an app id is configured.
/// </summary>
public sealed class TeamsServiceCollectionExtensionsTests
{
    private static IServiceProvider Build(string? appId)
    {
        var settings = new Dictionary<string, string?>();
        if (appId is not null)
        {
            settings[$"{TeamsServiceCollectionExtensions.ConfigSection}:AppId"] = appId;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTeamsAdapter(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Configured_Registers_ChatAdapter()
    {
        var provider = Build("00000000-0000-0000-0000-000000000000");
        provider.GetServices<IChatAdapter>().ShouldHaveSingleItem().Name.ShouldBe("Teams");
    }

    [Fact]
    public void Unconfigured_Registers_NoChatAdapter()
    {
        var provider = Build(null);
        provider.GetServices<IChatAdapter>().ShouldBeEmpty();
    }
}
