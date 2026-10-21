using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Scout.Skills.Tests;

/// <summary>
/// Verifies the tool-based skills layer wires into DI without coupling to the
/// embedder: the catalog and the "Skills" <see cref="KernelPlugin"/> resolve even
/// when the embedding generator would throw on any call, proving the plugin path
/// stays embedding-free so the shared kernel can aggregate it without a cycle.
/// </summary>
public sealed class SkillsServiceCollectionExtensionsTests
{
    // An embedder that fails loudly if ever touched while resolving the catalog or
    // plugin. The tool-based path must never reach it.
    private static IEmbeddingGenerator<string, Embedding<float>> ThrowingEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder
            .When(e => e.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("Embedder must not be called."));
        return embedder;
    }

    private static ServiceProvider Build(bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Skills:Enabled"] = enabled ? "true" : "false",
                ["Scout:Skills:Directory"] = "skills",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ThrowingEmbedder());
        services.AddScoutSkills(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Enabled_Registers_Skills_KernelPlugin_Without_Touching_Embedder()
    {
        using var provider = Build(enabled: true);

        var plugins = provider.GetServices<KernelPlugin>().ToList();

        plugins.ShouldContain(p => p.Name == "Skills");
    }

    [Fact]
    public void Enabled_Resolves_Catalog_Without_Touching_Embedder()
    {
        using var provider = Build(enabled: true);

        var catalog = provider.GetRequiredService<ISkillCatalog>();

        catalog.ShouldNotBeNull();
        // Forcing a load must still not reach the embedder.
        Should.NotThrow(() => catalog.All());
    }

    [Fact]
    public void Disabled_Registers_Neither_Catalog_Nor_Plugin()
    {
        using var provider = Build(enabled: false);

        provider.GetService<ISkillCatalog>().ShouldBeNull();
        provider.GetServices<KernelPlugin>().ShouldNotContain(p => p.Name == "Skills");
    }
}
