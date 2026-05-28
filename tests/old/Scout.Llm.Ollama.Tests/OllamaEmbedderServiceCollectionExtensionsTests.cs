using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scout.Plugin.CodeQA;
using Scout.Plugin.CodeQA.Embedding;

namespace Scout.Llm.Ollama.Tests;

public class OllamaEmbedderServiceCollectionExtensionsTests
{
    [Fact]
    public void AddOllamaEmbedder_Registers_Embedder()
    {
        var services = new ServiceCollection();
        services.Configure<CodeQAOptions>(_ => { });

        services.AddOllamaEmbedder();

        using var provider = services.BuildServiceProvider();
        var embedder = provider.GetRequiredService<IEmbedder>();

        embedder.ShouldBeOfType<OllamaEmbedder>();
    }

    [Fact]
    public void AddOllamaEmbedder_Registers_As_Singleton()
    {
        var services = new ServiceCollection();
        services.Configure<CodeQAOptions>(_ => { });

        services.AddOllamaEmbedder();

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IEmbedder>();
        var second = provider.GetRequiredService<IEmbedder>();

        first.ShouldBeSameAs(second);
    }

    [Fact]
    public void AddOllamaEmbedder_Null_Services_Throws()
    {
        Action act = () =>
            OllamaEmbedderServiceCollectionExtensions.AddOllamaEmbedder(null!);

        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void AddOllamaEmbedder_Honors_Bound_Options()
    {
        var services = new ServiceCollection();
        services.Configure<CodeQAOptions>(o =>
        {
            o.Embeddings.Model = "nomic-embed-text";
            o.Embeddings.BaseUrl = "http://127.0.0.1:11434";
            o.Embeddings.HttpTimeoutSeconds = 10;
            o.Embeddings.MaxBatchSize = 8;
        });

        services.AddOllamaEmbedder();

        using var provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IEmbedder>();

        act.ShouldNotThrow();
    }
}
