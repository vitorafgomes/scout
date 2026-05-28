using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Abstractions;

namespace Scout.Llm.Tests;

public sealed class LlmServiceCollectionExtensionsTests
{
    private static IConfiguration Config(string provider = "Ollama") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = provider,
                ["Scout:Llm:Ollama:Endpoint"] = "http://localhost:11434",
                ["Scout:Llm:Ollama:Chat:Model"] = "llama3.1",
                ["Scout:Llm:Ollama:Embeddings:Model"] = "nomic-embed-text",
            })
            .Build();

    [Fact]
    public void AddScoutLlm_Throws_When_Services_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddScoutLlm(Config()));
    }

    [Fact]
    public void AddScoutLlm_Throws_When_Configuration_Null()
    {
        Should.Throw<ArgumentNullException>(
            () => new ServiceCollection().AddScoutLlm(null!));
    }

    [Fact]
    public void AddScoutLlm_Resolves_ProviderFactory()
    {
        using var provider = new ServiceCollection().AddScoutLlm(Config()).BuildServiceProvider();

        var factory = provider.GetRequiredService<ILlmProviderFactory>();
        factory.Name.ShouldBe("Ollama");
    }

    [Fact]
    public void AddScoutLlm_Resolves_Shared_Kernel()
    {
        using var provider = new ServiceCollection().AddScoutLlm(Config()).BuildServiceProvider();

        var first = provider.GetRequiredService<Kernel>();
        var second = provider.GetRequiredService<Kernel>();

        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public void AddScoutLlm_Resolves_ChatCompletion_From_Kernel()
    {
        using var provider = new ServiceCollection().AddScoutLlm(Config()).BuildServiceProvider();

        provider.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void AddScoutLlm_Resolves_Embedding_Generator_From_Kernel()
    {
        using var provider = new ServiceCollection().AddScoutLlm(Config()).BuildServiceProvider();

        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .ShouldNotBeNull();
    }

    [Fact]
    public void AddScoutLlm_Throws_For_Unknown_Provider()
    {
        Should.Throw<InvalidOperationException>(
            () => new ServiceCollection().AddScoutLlm(Config("Nonexistent")))
            .Message.ShouldContain("Nonexistent");
    }
}
