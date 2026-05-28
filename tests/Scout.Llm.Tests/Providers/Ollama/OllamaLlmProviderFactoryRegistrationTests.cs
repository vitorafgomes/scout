using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scout.Llm.Configuration;
using Scout.Llm.Providers.Ollama;

namespace Scout.Llm.Tests.Providers.Ollama;

public sealed class OllamaLlmProviderFactoryRegistrationTests
{
    private static OllamaLlmProviderFactory CreateFactory() => new(Options.Create(new LlmOptions
    {
        Provider = "Ollama",
        Ollama = new OllamaOptions
        {
            Endpoint = "http://localhost:11434",
            Chat = new ChatModelOptions { Model = "llama3.1" },
            Embeddings = new EmbeddingModelOptions { Model = "nomic-embed-text" },
        },
    }));

    [Fact]
    public void AddToKernel_Registers_ChatCompletion_Service()
    {
        var builder = Kernel.CreateBuilder();

        CreateFactory().AddToKernel(builder);
        var kernel = builder.Build();

        kernel.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void AddToKernel_Registers_Embedding_Generator()
    {
        var builder = Kernel.CreateBuilder();

        CreateFactory().AddToKernel(builder);
        var kernel = builder.Build();

        kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .ShouldNotBeNull();
    }
}
