using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Knowledge;

/// <summary>
/// Resolves a project name to a <see cref="ProjectEntry"/> and lazily indexes
/// the project on first use. The merged map (discovered roots plus explicit
/// items, explicit items winning on name collision) is built once at
/// construction. Indexing is deferred to the first resolution to avoid an upfront embedding cost
/// on startup; a project is indexed again when its path changed since its chunks were stored.
/// </summary>
public sealed class ProjectResolutionService
{
    private readonly ProjectIndexer _indexer;
    private readonly IProjectKnowledgeStore _store;
    private readonly ILogger<ProjectResolutionService> _logger;
    private readonly Dictionary<string, ProjectEntry> _entries;
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private IReadOnlyList<string> _names;

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

    /// <summary>Returns all resolvable project names (discovered, explicit and registered at run time), sorted.</summary>
    public IReadOnlyList<string> GetProjectNames() => _names;

    /// <summary>Resolves a project name to its entry without indexing; null when unknown.</summary>
    public ProjectEntry? Find(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(projectName, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// Add a project at run time, such as a repository snapshot taken from chat, or replace one of the same name with
    /// another path: it is then indexed again on its next use, so a new snapshot of the same repository never answers
    /// from the old tree; the same name at the same path is left as it is.
    /// </summary>
    public void Register(ProjectEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.RepoPath))
        {
            throw new ArgumentException("A project needs a name and a path.", nameof(entry));
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Name, out var existing) && string.Equals(existing.RepoPath, entry.RepoPath, StringComparison.Ordinal))
            {
                // The same tree again: nothing to index anew.
                return;
            }

            _entries[entry.Name] = entry;
            _stale.Add(entry.Name);
            _names = _entries.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>
    /// Resolves a project name to its entry, indexing it on first use and again when the store's chunks came from
    /// another repository path than the one the entry has now. A database written before the path was recorded adopts
    /// the entry's path without indexing, and still answers when that path cannot be written. When indexing fails, a project whose index came from the entry's own path is
    /// still returned; one with no index, or an index of another path, returns null, as an unknown project does, and
    /// <see cref="Find"/> tells the two apart; either way it is indexed again on its next use. A provider without embeddings still indexes.
    /// </summary>
    public async Task<ProjectEntry?> ResolveAsync(string projectName, CancellationToken ct = default)
    {
        var entry = Find(projectName);
        if (entry is null)
        {
            _logger.LogWarning("Unknown project requested: {Project}", projectName);
            return null;
        }

        bool stale;
        lock (_gate)
        {
            stale = _stale.Contains(entry.Name);
        }

        var indexed = await _store.HasProjectAsync(entry.Name, ct).ConfigureAwait(false);
        if (indexed && !stale && await IsIndexedFromAsync(entry, ct).ConfigureAwait(false))
        {
            return entry;
        }

        try
        {
            await _indexer.IndexAsync(entry, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_entries.TryGetValue(entry.Name, out var registered)
                    && string.Equals(registered.RepoPath, entry.RepoPath, StringComparison.Ordinal))
                {
                    _stale.Remove(entry.Name);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (indexed && string.Equals(
                    await _store.GetIndexedPathAsync(entry.Name, ct).ConfigureAwait(false), entry.RepoPath, StringComparison.Ordinal))
            {
                _logger.LogError(ex, "Indexing failed for project {Project}; answering from the index it already has.", entry.Name);
                return entry;
            }

            _logger.LogError(ex, "Indexing failed for project {Project}, which has no index of its current path to answer from.", entry.Name);
            return null;
        }

        return entry;
    }

    private async Task<bool> IsIndexedFromAsync(ProjectEntry entry, CancellationToken ct)
    {
        var recorded = await _store.GetIndexedPathAsync(entry.Name, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(recorded))
        {
            try
            {
                await _store.SetIndexedPathAsync(entry.Name, entry.RepoPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not record the path project {Project} is indexed from; it is recorded on a later use.", entry.Name);
            }

            return true;
        }

        return string.Equals(recorded, entry.RepoPath, StringComparison.Ordinal);
    }
}
