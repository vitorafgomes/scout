using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
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
        // AddScoutLlm supplies the shared kernel in production; the agent factory
        // needs it to resolve.
        services.AddSingleton(Kernel.CreateBuilder().Build());
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
    public void Registers_Search_Project_Plugin_When_At_Least_One_Project_Configured()
    {
        using var provider = Build(
            ("Scout:Projects:Items:0:Name", "scout"),
            ("Scout:Projects:Items:0:RepoPath", "/repos/scout"));

        var plugin = provider.GetServices<KernelPlugin>()
            .SingleOrDefault(p => p.Name == "ProjectKnowledge");

        plugin.ShouldNotBeNull();
        plugin!.TryGetFunction("search_project", out _).ShouldBeTrue();
    }

    [Fact]
    public void Does_Not_Register_Search_Project_Plugin_When_No_Projects_Configured()
    {
        using var provider = Build();

        var plugin = provider.GetServices<KernelPlugin>()
            .SingleOrDefault(p => p.Name == "ProjectKnowledge");

        plugin.ShouldBeNull();
    }

    [Fact]
    public void Does_Not_Register_Agent_When_No_Projects_Configured()
    {
        using var provider = Build();

        provider.GetService<ChatCompletionAgent>().ShouldBeNull();
    }

    [Fact]
    public void Registers_Agent_When_At_Least_One_Project_Configured()
    {
        using var provider = Build(
            ("Scout:Projects:Items:0:Name", "scout"),
            ("Scout:Projects:Items:0:RepoPath", "/repos/scout"));

        provider.GetService<ChatCompletionAgent>().ShouldNotBeNull();
    }

    [Fact]
    public void Does_Not_Register_WatchService_When_Disabled()
    {
        using var provider = Build(
            ("Scout:Projects:Items:0:Name", "scout"),
            ("Scout:Projects:Items:0:RepoPath", "/repos/scout"));

        provider.GetServices<IHostedService>()
            .OfType<ProjectWatchService>()
            .ShouldBeEmpty();
    }

    [Fact]
    public void Does_Not_Register_WatchService_When_Enabled_But_No_Projects()
    {
        using var provider = Build(
            ("Scout:Projects:Watch:Enabled", "true"));

        provider.GetServices<IHostedService>()
            .OfType<ProjectWatchService>()
            .ShouldBeEmpty();
    }

    [Fact]
    public void Registers_WatchService_When_Enabled_And_At_Least_One_Project()
    {
        using var provider = Build(
            ("Scout:Projects:Watch:Enabled", "true"),
            ("Scout:Projects:Items:0:Name", "scout"),
            ("Scout:Projects:Items:0:RepoPath", "/repos/scout"));

        provider.GetServices<IHostedService>()
            .OfType<ProjectWatchService>()
            .ShouldHaveSingleItem();
    }

    [Fact]
    public void BuildingProvider_With_Watch_Enabled_Does_No_Io_Or_Embedding()
    {
        // No-startup-hang guard: resolving the graph must not touch the filesystem,
        // create a FileSystemWatcher, or call the embedder. Watching only begins in
        // ExecuteAsync, which BuildServiceProvider never invokes.
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scout:Projects:Watch:Enabled"] = "true",
                ["Scout:Projects:Items:0:Name"] = "scout",
                ["Scout:Projects:Items:0:RepoPath"] = "/nonexistent/repo/path",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(embedder);
        services.AddSingleton(Kernel.CreateBuilder().Build());
        services.AddProjectKnowledge(configuration);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetServices<IHostedService>().OfType<ProjectWatchService>().Single();

        embedder.ReceivedCalls().ShouldBeEmpty();
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
