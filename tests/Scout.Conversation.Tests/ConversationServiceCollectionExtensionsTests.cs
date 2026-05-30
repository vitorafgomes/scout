using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Memory;
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

    [Fact]
    public void Resolves_Memory_Store_And_Key_Provider_As_Singletons()
    {
        using var provider = Build();

        var store = provider.GetRequiredService<IConversationMemoryStore>();
        var keyProvider = provider.GetRequiredService<IConversationKeyProvider>();

        store.ShouldBeOfType<InMemoryConversationMemoryStore>();
        keyProvider.ShouldBeOfType<ConversationKeyProvider>();
        provider.GetRequiredService<IConversationMemoryStore>().ShouldBeSameAs(store);
        provider.GetRequiredService<IConversationKeyProvider>().ShouldBeSameAs(keyProvider);
    }

    [Fact]
    public void Binds_Memory_Options_From_Config_Section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Conversation:Memory:Enabled"] = "false",
                ["Scout:Conversation:Memory:MaxTurns"] = "3",
                ["Scout:Conversation:Memory:MaxReplayedChars"] = "1234",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatGateway>());
        services.AddScoutConversation(config);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ConversationMemoryOptions>>().Value;
        options.Enabled.ShouldBeFalse();
        options.MaxTurns.ShouldBe(3);
        options.MaxReplayedChars.ShouldBe(1234);
    }
}
