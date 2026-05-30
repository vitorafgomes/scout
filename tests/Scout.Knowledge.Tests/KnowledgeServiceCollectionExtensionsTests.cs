using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Scout.Knowledge;

namespace Scout.Knowledge.Tests;

/// <summary>
/// Verifies the project knowledge composition root registers the store, indexer,
/// retrieval service, and the search_project plugin as a resolvable KernelPlugin,
/// and binds configured projects.
/// </summary>
public sealed class KnowledgeServiceCollectionExtensionsTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config.Select(c =>
                new KeyValuePair<string, string?>(c.Key, c.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // AddScoutLlm supplies the embedder in production; substitute it here so
        // the knowledge graph resolves in isolation.
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddProjectKnowledge(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registers_Store()
    {
        using var provider = Build();

        provider.GetService<IProjectKnowledgeStore>().ShouldNotBeNull();
    }

    [Fact]
    public void Registers_Indexer_And_Service()
    {
        using var provider = Build();

        provider.GetService<ProjectIndexer>().ShouldNotBeNull();
        provider.GetService<ProjectKnowledgeService>().ShouldNotBeNull();
    }

    [Fact]
    public void Registers_Search_Project_Plugin()
    {
        using var provider = Build();

        var plugin = provider.GetServices<KernelPlugin>()
            .SingleOrDefault(p => p.Name == "ProjectKnowledge");

        plugin.ShouldNotBeNull();
        plugin!.TryGetFunction("search_project", out _).ShouldBeTrue();
    }

    [Fact]
    public void Binds_Configured_Projects()
    {
        using var provider = Build(
            ("Scout:Projects:Items:0:Name", "scout"),
            ("Scout:Projects:Items:0:RepoPath", "/repos/scout"));

        var options = provider.GetService<Microsoft.Extensions.Options.IOptions<ProjectsOptions>>();

        options.ShouldNotBeNull();
        var item = options!.Value.Items.ShouldHaveSingleItem();
        item.Name.ShouldBe("scout");
        item.RepoPath.ShouldBe("/repos/scout");
    }
}
