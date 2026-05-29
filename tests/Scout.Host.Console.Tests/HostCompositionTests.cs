using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scout.Adapter.Discord;
using Scout.Adapter.Slack;
using Scout.Adapter.Teams;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Llm;
using Scout.Llm.Abstractions;
using Xunit;
using Shouldly;

namespace Scout.Host.Console.Tests;

/// <summary>
/// DI smoke test for the host composition. Mirrors Program.cs registration and
/// asserts the graph resolves and the adapter set is gated by configuration.
/// Does not run the host or connect any bot.
/// </summary>
public sealed class HostCompositionTests
{
    private static readonly Dictionary<string, string?> CoreConfig = new()
    {
        ["Scout:Llm:Provider"] = "Ollama",
        ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
        ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
        ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
        ["Scout:Conversation:SystemPrompt"] = "You are Scout.",
    };

    private static ServiceProvider BuildProvider(
        IReadOnlyDictionary<string, string?>? adapterConfig = null)
    {
        var settings = new Dictionary<string, string?>(CoreConfig);
        if (adapterConfig is not null)
        {
            foreach (var pair in adapterConfig)
            {
                settings[pair.Key] = pair.Value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoutLlm(configuration);
        services.AddScoutConversation(configuration);
        services.AddDiscordAdapter(configuration);
        services.AddSlackAdapter(configuration);
        services.AddTeamsAdapter(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task NoAdapterConfig_Registers_NoChatAdapter()
    {
        await using var provider = BuildProvider();
        provider.GetServices<IChatAdapter>().ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscordConfig_Registers_ExactlyOneAdapter()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Scout:Adapters:Discord:BotToken"] = "test-token-not-real",
        });

        var adapters = provider.GetServices<IChatAdapter>().ToList();
        adapters.Count.ShouldBe(1);
        adapters[0].Name.ShouldBe("Discord");
    }

    [Fact]
    public async Task DiscordAndSlackConfig_Registers_TwoAdapters()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Scout:Adapters:Discord:BotToken"] = "test-token-not-real",
            ["Scout:Adapters:Slack:BotToken"] = "xoxb-not-real",
            ["Scout:Adapters:Slack:AppLevelToken"] = "xapp-not-real",
        });

        provider.GetServices<IChatAdapter>().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Resolves_ConversationConductor()
    {
        await using var provider = BuildProvider();
        provider.GetRequiredService<ConversationConductor>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Resolves_ChatGateway()
    {
        await using var provider = BuildProvider();
        provider.GetRequiredService<IChatGateway>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Resolves_ConversationRouter()
    {
        await using var provider = BuildProvider();
        provider.GetRequiredService<IConversationRouter>().ShouldNotBeNull();
    }
}
