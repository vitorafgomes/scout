using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scout.Knowledge;

namespace Scout.Host.Console;

/// <summary>
/// One-shot indexing entry point used by the <c>index &lt;project&gt;</c> command.
/// Initializes the store, finds the configured project, indexes it, then returns a process exit code;
/// a failure to index, a timeout nobody asked for included, is logged and returned as a code; only a cancellation the caller asked for is thrown. Does not start adapters or the host loop.
/// </summary>
internal static class IndexEntryPoint
{
    /// <summary>Index the named project and return a process exit code.</summary>
    /// <param name="services">The built host service provider.</param>
    /// <param name="projectName">The configured project name to index.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 1 when indexing fails, 2 on a usage/configuration error.</returns>
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

        try
        {
            var store = services.GetRequiredService<IProjectKnowledgeStore>();
            await store.InitializeAsync(ct).ConfigureAwait(false);

            var indexer = services.GetRequiredService<ProjectIndexer>();
            await indexer.IndexAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Indexing failed for project '{Project}'.", entry.Name);
            return 1;
        }

        logger.LogInformation("Indexing complete for project '{Project}'.", entry.Name);
        return 0;
    }

    /// <summary>
    /// Index the named project until the host is asked to stop: Ctrl+C, SIGTERM and SIGQUIT stop the run through the
    /// host's lifetime, which is hooked to them without starting any of the host's services; a run stopped that way
    /// ends at its next await, logs one warning and returns 1.
    /// </summary>
    /// <param name="host">The built index host.</param>
    /// <param name="projectName">The configured project name to index.</param>
    /// <returns>0 on success, 1 when indexing fails or was stopped, 2 on a usage/configuration error.</returns>
    public static async Task<int> RunAsync(IHost host, string? projectName)
    {
        await host.Services.GetRequiredService<IHostLifetime>().WaitForStartAsync(CancellationToken.None).ConfigureAwait(false);
        var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        try
        {
            return await RunAsync(host.Services, projectName, stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Scout.Host.Console.Index")
                .LogWarning("Indexing of project '{Project}' was stopped before it finished.", projectName);
            return 1;
        }
    }
}
