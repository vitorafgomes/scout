using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Scout.Knowledge;

/// <summary>
/// Background service that watches each configured project's repository and re-indexes
/// individual files as they change. Opt-in via <see cref="WatchOptions.Enabled"/>. All
/// filesystem and embedding work happens in <see cref="ExecuteAsync"/>, never in the
/// constructor, so building the DI graph performs no I/O. Per-file failures are logged
/// and never crash the service.
/// </summary>
internal sealed class ProjectWatchService : BackgroundService
{
    private readonly ProjectIndexer _indexer;
    private readonly IProjectKnowledgeStore _store;
    private readonly ProjectsOptions _options;
    private readonly ILogger<ProjectWatchService> _logger;

    private readonly object _gate = new();
    private readonly HashSet<(string Project, string RelativePath)> _pending = new();
    private readonly List<FileSystemWatcher> _watchers = new();

    private volatile bool _isWatching;
    private bool _reindexOffLogged;

    /// <summary>True once <see cref="ExecuteAsync"/> has armed the watchers; for test synchronization.</summary>
    internal bool IsWatching => _isWatching;

    /// <summary>Initializes the service; stores dependencies only, performs no I/O.</summary>
    /// <param name="indexer">Per-file record builder.</param>
    /// <param name="store">Knowledge store receiving incremental updates.</param>
    /// <param name="options">Configured projects and watch settings.</param>
    /// <param name="logger">Logger.</param>
    public ProjectWatchService(
        ProjectIndexer indexer,
        IProjectKnowledgeStore store,
        IOptions<ProjectsOptions> options,
        ILogger<ProjectWatchService> logger)
    {
        _indexer = indexer;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var project in _options.Items)
        {
            TryArmWatcher(project);
        }

        if (_watchers.Count == 0)
        {
            return;
        }

        _isWatching = true;

        var debounce = TimeSpan.FromMilliseconds(Math.Max(1, _options.Watch.DebounceMilliseconds));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(debounce, stoppingToken).ConfigureAwait(false);
                await DrainAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void TryArmWatcher(ProjectEntry project)
    {
        if (!Directory.Exists(project.RepoPath))
        {
            _logger.LogWarning(
                "Skipping watch for project {Project}: repo path not found: {RepoPath}",
                project.Name,
                project.RepoPath);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(project.RepoPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };

            watcher.Created += (_, e) => Enqueue(project, e.FullPath);
            watcher.Changed += (_, e) => Enqueue(project, e.FullPath);
            watcher.Deleted += (_, e) => Enqueue(project, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Enqueue(project, e.OldFullPath);
                Enqueue(project, e.FullPath);
            };
            watcher.Error += (_, e) => OnWatcherError(project, watcher, e.GetException());

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start watcher for project {Project}.", project.Name);
        }
    }

    private void OnWatcherError(ProjectEntry project, FileSystemWatcher watcher, Exception exception)
    {
        _logger.LogWarning(exception, "Watcher error for project {Project}; re-arming.", project.Name);

        // Single re-arm attempt; if this also fails the project simply stops watching.
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-arm watcher for project {Project}.", project.Name);
        }
    }

    private void Enqueue(ProjectEntry project, string absolutePath)
    {
        var relativePath = ProjectFileFilter.ToRelativePath(project.RepoPath, absolutePath);
        if (!ProjectFileFilter.IsIndexable(relativePath))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Add((project.Name, relativePath));
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        (string Project, string RelativePath)[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var (projectName, relativePath) in batch)
        {
            ct.ThrowIfCancellationRequested();

            var project = _options.Items.FirstOrDefault(p => p.Name == projectName);
            if (project is null)
            {
                continue;
            }

            try
            {
                await ReindexFileAsync(project, relativePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NotSupportedException)
            {
                if (!_reindexOffLogged)
                {
                    _reindexOffLogged = true;
                    _logger.LogInformation("Project re-indexing is off: the AI provider has no embeddings.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to re-index {Path} in project {Project}; continuing.",
                    relativePath,
                    project.Name);
            }
        }
    }

    private async Task ReindexFileAsync(ProjectEntry project, string relativePath, CancellationToken ct)
    {
        var absolutePath = Path.Combine(project.RepoPath, relativePath);

        IReadOnlyList<ChunkRecord> records;
        if (File.Exists(absolutePath))
        {
            records = await _indexer.BuildRecordsForFileAsync(project, absolutePath, ct).ConfigureAwait(false);
        }
        else
        {
            // Deleted or moved away: clear the file's rows.
            records = Array.Empty<ChunkRecord>();
        }

        await _store.ReplaceFileAsync(project.Name, relativePath, records, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        base.Dispose();
    }
}
