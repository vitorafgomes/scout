using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Knowledge;

/// <summary>
/// Resolves a project name to a <see cref="ProjectEntry"/> and lazily indexes
/// the project on first use. The merged map (discovered roots plus explicit
/// items, explicit items winning on name collision) is built once at
/// construction. Indexing is deferred to the first resolution to avoid an
/// upfront embedding cost on startup.
/// </summary>
public sealed class ProjectResolutionService
{
    private readonly ProjectIndexer _indexer;
    private readonly IProjectKnowledgeStore _store;
    private readonly ILogger<ProjectResolutionService> _logger;
    private readonly IReadOnlyDictionary<string, ProjectEntry> _entries;
    private readonly IReadOnlyList<string> _names;

    /// <summary>Creates the resolution service and builds the merged project map.</summary>
    public ProjectResolutionService(
        IOptions<ProjectsOptions> options,
        ProjectDiscoveryService discovery,
        ProjectIndexer indexer,
        IProjectKnowledgeStore store,
        ILogger<ProjectResolutionService> logger)
    {
        _indexer = indexer;
        _store = store;
        _logger = logger;

        var opts = options.Value;
        var merged = new Dictionary<string, ProjectEntry>(StringComparer.OrdinalIgnoreCase);

        // Discovered repositories first; explicit items override below. Discovery is
        // directory-listing only (no embedding, no indexing) so it is safe to run
        // synchronously at construction.
        foreach (var root in opts.Roots)
        {
            var discovered = discovery.DiscoverAsync(root, CancellationToken.None)
                .GetAwaiter().GetResult();
            foreach (var entry in discovered)
            {
                merged[entry.Name] = entry;
            }
        }

        // Explicit items win on name collision.
        foreach (var item in opts.Items)
        {
            merged[item.Name] = item;
        }

        _entries = merged;
        _names = merged.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Returns all resolvable project names (discovered plus explicit), sorted.</summary>
    public IReadOnlyList<string> GetProjectNames() => _names;

    /// <summary>Resolves a project name to its entry without indexing; null when unknown.</summary>
    public ProjectEntry? Find(string projectName) =>
        !string.IsNullOrWhiteSpace(projectName) && _entries.TryGetValue(projectName, out var entry)
            ? entry
            : null;

    /// <summary>
    /// Resolves a project name to its entry, indexing it on first use.
    /// Returns null when the project is unknown or when first-use indexing
    /// fails; never throws for those cases. A <see cref="NotSupportedException"/>
    /// from a provider without embeddings propagates.
    /// </summary>
    public async Task<ProjectEntry?> ResolveAsync(string projectName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectName) || !_entries.TryGetValue(projectName, out var entry))
        {
            _logger.LogWarning("Unknown project requested: {Project}", projectName);
            return null;
        }

        var alreadyIndexed = await _store.HasProjectAsync(entry.Name, ct).ConfigureAwait(false);
        if (alreadyIndexed)
        {
            return entry;
        }

        try
        {
            await _indexer.IndexAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NotSupportedException)
        {
            _logger.LogError(ex, "Lazy indexing failed for project {Project}", entry.Name);
            return null;
        }

        return entry;
    }
}
