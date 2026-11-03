using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scout.Conversation.Abstractions;
using Scout.Conversation.Access;
using Scout.Conversation.Memory;
using Scout.Conversation.Streaming;
using Scout.Conversation.Tests.Fakes;
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

    [Fact]
    public async Task Resolved_Conductor_Reads_The_Pairing_Code_And_Expiry_From_The_Scout_Access_Section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Access:PairingCode"] = "482913",
                ["Scout:Access:PairingExpiresAt"] = "2999-01-01T00:10:00.000Z",
            })
            .Build();
        var signal = Substitute.For<IPairingSignal>();
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatGateway>());
        services.AddSingleton(signal);
        services.AddScoutConversation(config);
        using var provider = services.BuildServiceProvider();
        var sink = new RecordingResponseSink(new RenderCapabilities { FineGrainedEdit = true });

        await provider.GetRequiredService<ConversationConductor>().HandleAsync(
            new ChatMessage("111", "c1", "pair 482913", IsDirectMessage: true), sink, CancellationToken.None);

        sink.FinalText.ShouldBe("Paired. Scout will answer you from now on.");
    }

    [Theory]
    [InlineData("Scout:Access:RequireAllowList", "true",
        "Scout is not set up for anyone yet. If you are the owner, open the Scout desktop app, go to Settings, "
        + "choose Pair from chat and send the 6-digit code here in a direct message.")]
    [InlineData("Scout:Access:AllowedUserIds:0", "111", "Scout only answers its configured users.")]
    public async Task Resolved_Conductor_Applies_The_Scout_Access_Section(
        string key, string value, string expectedReply)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
        var gateway = Substitute.For<IChatGateway>();
        var services = new ServiceCollection();
        services.AddSingleton(gateway);
        services.AddScoutConversation(config);
        using var provider = services.BuildServiceProvider();
        var sink = new RecordingResponseSink(new RenderCapabilities { FineGrainedEdit = true });

        await provider.GetRequiredService<ConversationConductor>().HandleAsync(
            new ChatMessage("222", "c1", "hi"), sink, CancellationToken.None);

        sink.FinalText.ShouldBe(expectedReply);
        gateway.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolved_Origin_Notifier_Posts_Through_The_Poster_An_Adapter_Registered()
    {
        var poster = Substitute.For<IOriginPoster>();
        poster.Kind.Returns("slack");
        poster.PostAsync("C1:1.2", "Video rendered.", Arg.Any<CancellationToken>()).Returns(true);
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatGateway>());
        services.AddSingleton(poster);
        services.AddScoutConversation(Config());
        using var provider = services.BuildServiceProvider();

        var posted = await provider.GetRequiredService<IOriginNotifier>()
            .PostAsync("slack:C1:1.2", "Video rendered.", CancellationToken.None);

        posted.ShouldBeTrue();
    }
}
