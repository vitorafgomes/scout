using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Streaming;
using Scout.Llm.Abstractions;
using Scout.Skills;

namespace Scout.Conversation.Tests;

public sealed class ConversationServiceCollectionExtensionsTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Conversation:SystemPrompt"] = "You are Scout.",
                ["Scout:Conversation:Temperature"] = "0.3",
                ["Scout:Conversation:MaxTokens"] = "500",
            })
            .Build();

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        // The gateway is provided by AddScoutLlm in production; substitute here.
        services.AddSingleton(Substitute.For<IChatGateway>());
        services.AddScoutConversation(Config());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddScoutConversation_Throws_When_Services_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddScoutConversation(Config()));
    }

    [Fact]
    public void AddScoutConversation_Throws_When_Configuration_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddScoutConversation(null!));
    }

    [Fact]
    public void Resolves_Router_Pump_And_Conductor()
    {
        using var provider = Build();

        provider.GetRequiredService<IConversationRouter>().ShouldNotBeNull();
        provider.GetRequiredService<StreamPump>().ShouldNotBeNull();
        provider.GetRequiredService<ConversationConductor>().ShouldNotBeNull();
    }

    [Fact]
    public void Registers_NoOp_SkillSelector_As_Fallback()
    {
        using var provider = Build();

        provider.GetRequiredService<ISkillSelector>().ShouldBeOfType<NoOpSkillSelector>();
    }

    [Fact]
    public void Does_Not_Register_An_Adapter()
    {
        using var provider = Build();

        provider.GetService<IChatAdapter>().ShouldBeNull();
    }
}
