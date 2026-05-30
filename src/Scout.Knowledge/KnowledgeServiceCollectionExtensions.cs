using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace Scout.Knowledge;

/// <summary>
/// DI composition for project knowledge: the SQLite-vec store, the file chunker,
/// the indexer, the retrieval service, the search_project Semantic Kernel plugin,
/// and the Project Expert chat agent. The plugin is registered as a
/// <see cref="KernelPlugin"/> so the shared kernel composition picks it up.
/// </summary>
public static class KnowledgeServiceCollectionExtensions
{
    private const string DatabaseFileName = "project-knowledge.db";

    /// <summary>
    /// Register project knowledge services. Binds <see cref="ProjectsOptions"/>
    /// from <see cref="ProjectsOptions.SectionName"/> and stores the vector
    /// database under the host's local application data directory.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddProjectKnowledge(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ProjectsOptions>(configuration.GetSection(ProjectsOptions.SectionName));

        services.AddSingleton<IProjectKnowledgeStore>(
            new SqliteProjectKnowledgeStore($"Data Source={ResolveDatabasePath()}"));

        services.AddSingleton<IChunker, FileChunker>();

        services.AddSingleton(sp => new ProjectIndexer(
            sp.GetRequiredService<IChunker>(),
            sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            sp.GetRequiredService<IProjectKnowledgeStore>(),
            sp.GetRequiredService<ILogger<ProjectIndexer>>()));

        services.AddSingleton(sp => new ProjectKnowledgeService(
            sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            sp.GetRequiredService<IProjectKnowledgeStore>(),
            sp.GetRequiredService<ILogger<ProjectKnowledgeService>>()));

        services.AddSingleton(sp => KernelPluginFactory.CreateFromObject(
            new ProjectKnowledgePlugin(sp.GetRequiredService<ProjectKnowledgeService>()),
            "ProjectKnowledge"));

        // Gate the agent on configured projects. With none, the gateway resolves
        // no ChatCompletionAgent and falls back to general chat. The agent binds
        // the fully-composed shared kernel (which carries the search_project plugin).
        var projects = new ProjectsOptions();
        configuration.GetSection(ProjectsOptions.SectionName).Bind(projects);
        if (projects.Items.Count > 0)
        {
            services.AddSingleton(sp =>
                ProjectExpertAgentFactory.Create(sp.GetRequiredService<Kernel>()));
        }

        return services;
    }

    private static string ResolveDatabasePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(root, "Scout");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, DatabaseFileName);
    }
}
