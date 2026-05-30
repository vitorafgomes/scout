using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scout.Knowledge;

namespace Scout.Host.Console;

/// <summary>
/// One-shot indexing entry point used by the <c>index &lt;project&gt;</c> command.
/// Initializes the store, finds the configured project, indexes it, then returns
/// a process exit code. Does not start adapters or the host loop.
/// </summary>
internal static class IndexEntryPoint
{
    /// <summary>Index the named project and return a process exit code.</summary>
    /// <param name="services">The built host service provider.</param>
    /// <param name="projectName">The configured project name to index.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 2 on a usage/configuration error.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string? projectName,
        CancellationToken ct)
    {
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Scout.Host.Console.Index");

        if (string.IsNullOrWhiteSpace(projectName))
        {
            logger.LogError("Usage: index <project>");
            return 2;
        }

        var entry = services.GetRequiredService<ProjectResolutionService>().Find(projectName);

        if (entry is null)
        {
            logger.LogError("Unknown project '{Project}'. Configure it under {Section}.",
                projectName, ProjectsOptions.SectionName);
            return 2;
        }

        var store = services.GetRequiredService<IProjectKnowledgeStore>();
        await store.InitializeAsync(ct).ConfigureAwait(false);

        var indexer = services.GetRequiredService<ProjectIndexer>();
        await indexer.IndexAsync(entry, ct).ConfigureAwait(false);

        logger.LogInformation("Indexing complete for project '{Project}'.", entry.Name);
        return 0;
    }
}
