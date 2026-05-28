using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scout.Adapter.Discord;
using Scout.Conversation;
using Scout.Conversation.Abstractions;
using Scout.Llm;
using Scout.Llm.Abstractions;
using Xunit;
using Shouldly;

namespace Scout.Host.Console.Tests;

/// <summary>
/// DI smoke test for the host composition. Mirrors Program.cs registration and
/// asserts the graph resolves. Does not run the host or connect any bot.
/// </summary>
public sealed class HostCompositionTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Ollama",
                ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
                ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
                ["Scout:Conversation:SystemPrompt"] = "You are Scout.",
                // A non-empty token so the adapter constructor does not reject.
                ["Scout:Adapters:Discord:BotToken"] = "test-token-not-real",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoutLlm(configuration);
        services.AddScoutConversation(configuration);
        services.AddDiscordAdapter(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Resolves_ChatAdapter()
    {
        await using var provider = BuildProvider();
        provider.GetRequiredService<IChatAdapter>().Name.ShouldBe("Discord");
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
