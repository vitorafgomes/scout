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
    public void AddScoutLlm_Resolves_ChatGateway()
    {
        using var provider = new ServiceCollection().AddScoutLlm(Config()).BuildServiceProvider();

        provider.GetRequiredService<IChatGateway>().ShouldNotBeNull();
    }

    private static IConfiguration GeminiConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Gemini",
                ["Scout:Llm:Gemini:ApiKey"] = "test-key",
                ["Scout:Llm:Gemini:Chat:Model"] = "gemini-1.5-flash",
                ["Scout:Llm:Gemini:Embeddings:Model"] = "text-embedding-004",
            })
            .Build();

    [Fact]
    public void AddScoutLlm_Resolves_GeminiProviderFactory()
    {
        using var provider = new ServiceCollection()
            .AddScoutLlm(GeminiConfig()).BuildServiceProvider();

        var factory = provider.GetRequiredService<ILlmProviderFactory>();
        factory.Name.ShouldBe("Gemini");
    }

    [Fact]
    public void AddScoutLlm_Does_Not_Throw_For_Gemini_Provider()
    {
        Should.NotThrow(() => new ServiceCollection().AddScoutLlm(GeminiConfig()));
    }

    private static IConfiguration OpenAIConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "OpenAI",
                ["Scout:Llm:OpenAI:ApiKey"] = "test-key",
                ["Scout:Llm:OpenAI:Chat:Model"] = "gpt-4o-mini",
                ["Scout:Llm:OpenAI:Embeddings:Model"] = "text-embedding-3-small",
            })
            .Build();

    private static IConfiguration AzureOpenAIConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "AzureOpenAI",
                ["Scout:Llm:AzureOpenAI:ApiKey"] = "test-key",
                ["Scout:Llm:AzureOpenAI:Endpoint"] = "https://my-aoai.openai.azure.com",
                ["Scout:Llm:AzureOpenAI:Chat:Deployment"] = "gpt-4o",
                ["Scout:Llm:AzureOpenAI:Embeddings:Deployment"] = "text-embedding-3-small",
            })
            .Build();

    private static IConfiguration MistralConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "Mistral",
                ["Scout:Llm:Mistral:ApiKey"] = "test-key",
                ["Scout:Llm:Mistral:Chat:Model"] = "mistral-small-latest",
                ["Scout:Llm:Mistral:Embeddings:Model"] = "mistral-embed",
            })
            .Build();

    private static IConfiguration HuggingFaceConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "HuggingFace",
                ["Scout:Llm:HuggingFace:ApiKey"] = "test-key",
                ["Scout:Llm:HuggingFace:Chat:Model"] = "meta-llama/Llama-3.1-8B-Instruct",
                ["Scout:Llm:HuggingFace:Embeddings:Model"] = "sentence-transformers/all-MiniLM-L6-v2",
            })
            .Build();

    private static IConfiguration AzureAIInferenceConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Llm:Provider"] = "AzureAIInference",
                ["Scout:Llm:AzureAIInference:ApiKey"] = "test-key",
                ["Scout:Llm:AzureAIInference:Endpoint"] = "https://my-inference.services.ai.azure.com/models",
                ["Scout:Llm:AzureAIInference:Chat:Model"] = "Phi-3.5-MoE-instruct",
                ["Scout:Llm:AzureAIInference:Embeddings:Model"] = "text-embedding-3-small",
            })
            .Build();

    public static TheoryData<string, Func<IConfiguration>> ProviderConfigs() => new()
    {
        { "OpenAI", OpenAIConfig },
        { "AzureOpenAI", AzureOpenAIConfig },
        { "Mistral", MistralConfig },
        { "HuggingFace", HuggingFaceConfig },
        { "AzureAIInference", AzureAIInferenceConfig },
    };

    [Theory]
    [MemberData(nameof(ProviderConfigs))]
    public void AddScoutLlm_Resolves_Expected_ProviderFactory(
        string expectedName, Func<IConfiguration> config)
    {
        using var provider = new ServiceCollection()
            .AddScoutLlm(config()).BuildServiceProvider();

        var factory = provider.GetRequiredService<ILlmProviderFactory>();
        factory.Name.ShouldBe(expectedName);
    }

    [Theory]
    [MemberData(nameof(ProviderConfigs))]
    public void AddScoutLlm_Does_Not_Throw_For_Supported_Provider(
        string expectedName, Func<IConfiguration> config)
    {
        _ = expectedName;
        Should.NotThrow(() => new ServiceCollection().AddScoutLlm(config()));
    }

    [Fact]
    public void AddScoutLlm_Throws_For_Unknown_Provider()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => new ServiceCollection().AddScoutLlm(Config("Nonexistent")));

        ex.Message.ShouldContain("Nonexistent");
        ex.Message.ShouldContain("Ollama");
        ex.Message.ShouldContain("Gemini");
        ex.Message.ShouldContain("OpenAI");
        ex.Message.ShouldContain("AzureOpenAI");
        ex.Message.ShouldContain("Mistral");
        ex.Message.ShouldContain("HuggingFace");
        ex.Message.ShouldContain("AzureAIInference");
    }

    [Fact]
    public void AddScoutLlm_Composes_Registered_KernelPlugins_Onto_Kernel()
    {
        var services = new ServiceCollection().AddScoutLlm(Config());
        var plugin = KernelPluginFactory.CreateFromFunctions(
            "TestPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => "ok", "ping")]);
        services.AddSingleton(plugin);

        using var provider = services.BuildServiceProvider();
        var kernel = provider.GetRequiredService<Kernel>();

        kernel.Plugins.ShouldContain(plugin);
    }
}
